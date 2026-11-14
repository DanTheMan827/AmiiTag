#!/usr/bin/env python3
"""
Host library and command line for the Flipper Zero NFC Bridge (protocol v2, see ../docs/PROTOCOL.md).

    pip install hidapi
    python nfc_bridge.py info                       # firmware, capabilities, counters
    python nfc_bridge.py read-a [--pages N]         # activate a type A card; NTAG/Ultralight pages too
    python nfc_bridge.py emulate-ntag dump.bin      # emulate an NTAG213/215/216 from a dump, logging the reader

Use as a library:

    from nfc_bridge import NfcBridge, TECH_A
    with NfcBridge.open() as bridge:
        bridge.reader(TECH_A)
        uid, atqa, sak = bridge.activate_a()
"""

from __future__ import annotations

import argparse
import queue
import struct
import sys
import threading
import time
from dataclasses import dataclass, field

VID, PID = 0x1209, 0x0001
USAGE_PAGE = 0xFF41
PRODUCT = "NFC Bridge"
PROTOCOL_VERSION = 2
REPORT, HEADER, CHUNK, MAX_PAYLOAD = 64, 6, 58, 1024

TECH_A, TECH_B, TECH_V, TECH_F = 0, 1, 2, 3
TECH_NAMES = {TECH_A: "ISO 14443-A", TECH_B: "ISO 14443-B", TECH_V: "ISO 15693", TECH_F: "FeliCa"}

# Message types
HELLO, ACK, FIELD_ON, FIELD_OFF, ACTIVATED, HALTED = 0x01, 0x02, 0x10, 0x11, 0x12, 0x13
FRAME, TRANSCEIVE_RESULT, STATUS, LOG = 0x20, 0x21, 0x30, 0x7F
HOST_HELLO, LISTEN, READER, STOP, GET_STATUS, SET_TIMING = 0x81, 0x82, 0x83, 0x84, 0x85, 0x86
TRANSCEIVE, FIELD_RESET, TABLE_CLEAR, TABLE_SET, TABLE_REMOVE, ANSWER = 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x90

# Flags
LISTEN_STRIP_CRC, LISTEN_HANDLE_HLTA, LISTEN_NOTIFY_TABLE_HITS, LISTEN_REPORT_PARITY = 1, 2, 4, 8
FRAME_CRC_OK, FRAME_PARITY, FRAME_FROM_TABLE = 1, 2, 4
TX_ADD_CRC, TX_SLEEP, TX_CUSTOM_PARITY, TX_CHECK_CRC = 1, 2, 4, 8
KIND_FRAME, KIND_REQA, KIND_WUPA, KIND_SDD, KIND_CUSTOM_PARITY = 0, 1, 2, 3, 4

STATUS_NAMES = ["Ok", "BadArguments", "Unsupported", "WrongMode", "TableFull", "NotFound"]
ERROR_NAMES = ["None", "Timeout", "IncompleteFrame", "DataFormat", "Internal", "WrongMode", "BadArguments"]
CAPABILITIES = ["listen A", "listen B", "listen V", "listen F", "reader A", "reader B", "reader V", "reader F",
                "answer table", "custom parity"]


class BridgeError(Exception):
    pass


def nbytes(bits: int) -> int:
    return (bits + 7) // 8


def encode(msg_type: int, seq: int, payload: bytes) -> list[bytes]:
    """Splits a message into 64 byte reports."""
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("payload too long")
    chunks = max(1, -(-len(payload) // CHUNK))
    reports = []
    for i in range(chunks):
        part = payload[i * CHUNK:(i + 1) * CHUNK]
        header = bytes([msg_type, seq & 0xFF, i, chunks]) + struct.pack("<H", len(payload))
        reports.append((header + part).ljust(REPORT, b"\0"))
    return reports


class Assembler:
    """Puts chunked messages back together; a missing chunk drops the message."""

    def __init__(self):
        self._msg = None

    def add(self, report: bytes):
        msg_type, seq, index, count = report[0], report[1], report[2], max(report[3], 1)
        length = struct.unpack_from("<H", report, 4)[0]
        if index == 0:
            self._msg = [msg_type, seq, count, length, bytearray(), 1]
        elif self._msg is None or index != self._msg[5] or msg_type != self._msg[0] or seq != self._msg[1]:
            self._msg = None
            return None
        else:
            self._msg[5] += 1
        m = self._msg
        m[4] += report[HEADER:HEADER + min(CHUNK, m[3] - len(m[4]))]
        if m[5] < m[2]:
            return None
        self._msg = None
        return m[0], m[1], bytes(m[4])


@dataclass
class Frame:
    """A frame from the reader (listen mode)."""
    seq: int
    timestamp_us: int
    flags: int
    bits: int
    data: bytes
    parity: bytes = b""

    @property
    def from_table(self) -> bool:
        return bool(self.flags & FRAME_FROM_TABLE)

    @property
    def crc_ok(self) -> bool:
        return bool(self.flags & FRAME_CRC_OK)


@dataclass
class Result:
    """The outcome of a TRANSCEIVE (reader mode)."""
    error: int
    flags: int
    bits: int
    elapsed_us: int
    data: bytes
    parity: bytes = b""

    @property
    def ok(self) -> bool:
        return self.error == 0

    @property
    def error_name(self) -> str:
        return ERROR_NAMES[self.error] if self.error < len(ERROR_NAMES) else str(self.error)


@dataclass
class Event:
    """FIELD_ON, FIELD_OFF, ACTIVATED, HALTED, LOG or FRAME."""
    type: int
    timestamp_us: int = 0
    frame: Frame | None = None
    text: str = ""


def _parse_frame(seq: int, p: bytes) -> Frame:
    ts, flags, bits = struct.unpack_from("<IBH", p, 0)
    n = nbytes(bits)
    data = p[7:7 + n]
    parity = p[7 + n:7 + n + nbytes(n)] if flags & FRAME_PARITY else b""
    return Frame(seq, ts, flags, bits, data, parity)


def _parse_result(p: bytes) -> Result:
    error, flags, bits, elapsed = struct.unpack_from("<BBHI", p, 0)
    n = nbytes(bits)
    data = p[9:9 + n]
    parity = p[9 + n:9 + n + nbytes(n)] if flags & FRAME_PARITY else b""
    return Result(error, flags, bits, elapsed, data, parity)


def _parity_bitmap(bits: list[int] | bytes) -> bytes:
    out = bytearray(nbytes(len(bits)))
    for i, b in enumerate(bits):
        if b:
            out[i // 8] |= 1 << (i % 8)
    return bytes(out)


class NfcBridge:
    """
    A connection to the Flipper NFC Bridge app. Call it from one thread, plus on_frame on the read thread, which may
    answer and change the answer table (without waiting for the acknowledgement).
    """

    def __init__(self, device):
        self._dev = device
        self._assembler = Assembler()
        self._replies: dict[int, queue.Queue] = {t: queue.Queue() for t in (HELLO, ACK, STATUS)}
        self._results: dict[int, queue.Queue] = {}
        self._events: queue.Queue = queue.Queue()
        self._write_lock = threading.Lock()
        self._seq = 0
        self._closed = False
        self.error: Exception | None = None
        self.on_frame = None  # optional callable(Frame) run on the read thread, for the fastest answers
        self._reader = threading.Thread(target=self._read_loop, name="nfc-bridge-read", daemon=True)
        self._reader.start()
        self.hello = self._hello()

    # ---- opening ----

    @staticmethod
    def find() -> list[dict]:
        import hid
        return [d for d in hid.enumerate(VID, PID)
                if d.get("usage_page") == USAGE_PAGE or PRODUCT in (d.get("product_string") or "")]

    @classmethod
    def open(cls, path: bytes | None = None) -> "NfcBridge":
        import hid
        if path is None:
            found = cls.find()
            if not found:
                raise BridgeError("No Flipper running the NFC Bridge app found")
            path = found[0]["path"]
        device = hid.device()
        device.open_path(path)
        return cls(_HidapiDevice(device))

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def close(self):
        if self._closed:
            return
        try:
            self.stop()
        except Exception:
            pass
        self._closed = True
        self._dev.close()

    # ---- plumbing ----

    def _send(self, msg_type: int, seq: int, payload: bytes = b""):
        with self._write_lock:
            for report in encode(msg_type, seq, payload):
                self._dev.write(report)

    def _read_loop(self):
        try:
            while not self._closed:
                report = self._dev.read(100)
                if not report:
                    continue
                msg = self._assembler.add(bytes(report))
                if msg:
                    self._dispatch(*msg)
        except Exception as ex:  # unplugged or closed
            if not self._closed:
                self.error = ex
                self._events.put(Event(LOG, text=f"connection lost: {ex}"))

    def _dispatch(self, msg_type: int, seq: int, p: bytes):
        if msg_type in self._replies:
            self._replies[msg_type].put(p)
        elif msg_type == TRANSCEIVE_RESULT:
            q = self._results.get(seq)
            if q:
                q.put(_parse_result(p))
        elif msg_type == FRAME:
            frame = _parse_frame(seq, p)
            if self.on_frame is not None and not frame.from_table:
                self.on_frame(frame)
            else:
                self._events.put(Event(FRAME, frame.timestamp_us, frame))
        elif msg_type in (FIELD_ON, FIELD_OFF, ACTIVATED, HALTED):
            self._events.put(Event(msg_type, struct.unpack_from("<I", p)[0] if len(p) >= 4 else 0))
        elif msg_type == LOG:
            self._events.put(Event(LOG, text=p.decode("ascii", "replace")))

    def _wait(self, msg_type: int, timeout: float = 2.0) -> bytes:
        try:
            return self._replies[msg_type].get(timeout=timeout)
        except queue.Empty:
            raise BridgeError(self.error and f"connection lost: {self.error}" or "no reply from the Flipper") from None

    def _control(self, msg_type: int, payload: bytes = b""):
        if threading.current_thread() is self._reader:
            # From on_frame: the read thread can't wait for its own ACK. Send and move on (the next waiting
            # call drains the ACK).
            self._send(msg_type, 0, payload)
            return
        while not self._replies[ACK].empty():
            self._replies[ACK].get_nowait()
        self._send(msg_type, 0, payload)
        acked, status = self._wait(ACK)[:2]
        if status != 0:
            name = STATUS_NAMES[status] if status < len(STATUS_NAMES) else str(status)
            raise BridgeError(f"message 0x{msg_type:02X} refused: {name}")

    def _hello(self) -> dict:
        self._send(HOST_HELLO, 0)
        p = self._wait(HELLO)
        version, _, max_payload, caps = struct.unpack_from("<BBHI", p, 0)
        if version != PROTOCOL_VERSION:
            raise BridgeError(f"Flipper app speaks protocol {version}, this host {PROTOCOL_VERSION}")
        return {"version": version, "max_payload": max_payload, "capabilities": caps,
                "firmware": p[8:].decode("ascii", "replace")}

    # ---- control ----

    def status(self) -> dict:
        self._send(GET_STATUS, 0)
        names = ("frames", "answered", "timeouts", "late", "worst_us", "table_hits", "transceives")
        return dict(zip(names, struct.unpack_from("<7I", self._wait(STATUS))))

    def stop(self):
        self._control(STOP)

    def set_timing(self, fdt_poll_fc=0, fdt_listen_fc=0, guard_us=0, poll_poll_us=0, mask_receive_fc=0):
        self._control(SET_TIMING, struct.pack("<5I", fdt_poll_fc, fdt_listen_fc, guard_us, poll_poll_us, mask_receive_fc))

    # ---- listen ----

    DEFAULT_LISTEN = LISTEN_STRIP_CRC | LISTEN_HANDLE_HLTA | LISTEN_NOTIFY_TABLE_HITS

    def listen_a(self, uid: bytes, atqa: bytes = b"\x44\x00", sak: int = 0x00, flags: int = DEFAULT_LISTEN,
                 timeout_ms: int = 25):
        if len(uid) not in (4, 7, 10):
            raise ValueError("UIDs are 4, 7 or 10 bytes")
        self._control(LISTEN, struct.pack("<BBH", TECH_A, flags, timeout_ms) + bytes([len(uid)]) + uid + atqa + bytes([sak]))

    def listen_v(self, flags: int = LISTEN_STRIP_CRC | LISTEN_NOTIFY_TABLE_HITS, timeout_ms: int = 25):
        self._control(LISTEN, struct.pack("<BBH", TECH_V, flags, timeout_ms))

    def listen_f(self, idm: bytes, pmm: bytes, system_code: int = 0xFFFF,
                 flags: int = LISTEN_STRIP_CRC | LISTEN_NOTIFY_TABLE_HITS, timeout_ms: int = 25):
        self._control(LISTEN, struct.pack("<BBH", TECH_F, flags, timeout_ms) + idm + pmm + struct.pack("<H", system_code))

    def answer(self, frame: Frame | int, data: bytes = b"", bits: int | None = None, add_crc: bool = True,
               sleep: bool = False, parity: bytes | None = None):
        """Answers a FRAME. Empty data stays silent; ack()/nak() give 4 bit answers."""
        seq = frame.seq if isinstance(frame, Frame) else frame
        bits = len(data) * 8 if bits is None else bits
        flags = (TX_ADD_CRC if add_crc and bits and bits % 8 == 0 else 0) | (TX_SLEEP if sleep else 0)
        if parity is not None:
            flags |= TX_CUSTOM_PARITY
        self._send(ANSWER, seq, struct.pack("<BH", flags, bits) + data + (parity or b""))

    def ack(self, frame: Frame):
        self.answer(frame, b"\x0A", bits=4, add_crc=False)

    def nak(self, frame: Frame, value: int = 0x0, sleep: bool = True):
        self.answer(frame, bytes([value & 0x0F]), bits=4, add_crc=False, sleep=sleep)

    def events(self, timeout: float | None = None):
        """Yields events (frames, field changes, logs) until timeout seconds pass without one."""
        while True:
            try:
                yield self._events.get(timeout=timeout)
            except queue.Empty:
                return

    # ---- answer table ----

    def table_clear(self):
        self._control(TABLE_CLEAR)

    def table_set(self, match: bytes, answer: bytes, bits: int | None = None, add_crc: bool = True,
                  sleep: bool = False, parity: bytes | None = None):
        bits = len(answer) * 8 if bits is None else bits
        flags = (TX_ADD_CRC if add_crc and bits % 8 == 0 else 0) | (TX_SLEEP if sleep else 0)
        if parity is not None:
            flags |= TX_CUSTOM_PARITY
        self._control(TABLE_SET, struct.pack("<BBH", flags, len(match), bits) + match + answer + (parity or b""))

    def table_remove(self, match: bytes):
        self._control(TABLE_REMOVE, bytes([len(match)]) + match)

    # ---- reader ----

    def reader(self, tech: int):
        self._control(READER, bytes([tech, 0]))

    def field_reset(self):
        self._control(FIELD_RESET)

    def transceive(self, data: bytes = b"", bits: int | None = None, kind: int = KIND_FRAME, add_crc: bool = False,
                   check_crc: bool = False, timeout_us: int = 5000, parity: bytes | None = None) -> Result:
        self._seq = (self._seq + 1) & 0xFF
        seq = self._seq
        bits = len(data) * 8 if bits is None else bits
        flags = (TX_ADD_CRC if add_crc else 0) | (TX_CHECK_CRC if check_crc else 0)
        if parity is not None:
            flags |= TX_CUSTOM_PARITY
        q: queue.Queue = queue.Queue()
        self._results[seq] = q
        try:
            self._send(TRANSCEIVE, seq, struct.pack("<BBHI", kind, flags, bits, timeout_us) + data + (parity or b""))
            return q.get(timeout=2 + timeout_us / 1e6)
        except queue.Empty:
            raise BridgeError("no TRANSCEIVE_RESULT") from None
        finally:
            del self._results[seq]

    def activate_a(self, wake: bool = True) -> tuple[bytes, bytes, int]:
        """ISO 14443-3 type A activation (no collision resolution: one card in the field). Returns (uid, atqa, sak)."""
        r = self.transceive(kind=KIND_WUPA if wake else KIND_REQA)
        if not r.ok:
            raise BridgeError(f"no card ({r.error_name})")
        atqa, uid = r.data, b""
        for level in (0x93, 0x95, 0x97):
            r = self.transceive(bytes([level, 0x20]), kind=KIND_SDD)
            if not r.ok or len(r.data) != 5:
                raise BridgeError(f"anticollision failed ({r.error_name})")
            part = r.data
            if part[0] ^ part[1] ^ part[2] ^ part[3] != part[4]:
                raise BridgeError("BCC mismatch (collision?)")
            r = self.transceive(bytes([level, 0x70]) + part, add_crc=True, check_crc=True)
            if not r.ok or not r.flags & FRAME_CRC_OK:
                raise BridgeError(f"SELECT failed ({r.error_name})")
            sak = r.data[0]
            if sak & 0x04:  # UID not complete: part starts with the cascade tag 88
                uid += part[1:4]
                continue
            return uid + part[:4], atqa, sak
        raise BridgeError("UID longer than three cascade levels")


class _HidapiDevice:
    """hidapi wrapper: adds/strips the report ID byte."""

    def __init__(self, device):
        self._d = device

    def write(self, report: bytes):
        if self._d.write(b"\0" + report) < 0:
            raise OSError("HID write failed")

    def read(self, timeout_ms: int) -> bytes:
        data = self._d.read(65, timeout_ms)
        if data and len(data) > REPORT:
            data = data[1:]
        return bytes(data)

    def close(self):
        self._d.close()


# ---- NTAG21x emulation (example) ----

NTAG_VERSIONS = {45: bytes.fromhex("0004040201000F03"), 135: bytes.fromhex("0004040201001103"),
                 231: bytes.fromhex("0004040201001303")}


@dataclass
class NtagEmulator:
    """A plain NTAG213/215/216 from a dump: READs and GET_VERSION from the answer table, the rest answered live."""
    memory: bytearray
    signature: bytes = bytes(32)
    pages: int = field(init=False)

    def __post_init__(self):
        self.pages = len(self.memory) // 4
        if self.pages not in NTAG_VERSIONS:
            raise ValueError("dump must be 180, 540 or 924 bytes (NTAG213/215/216)")
        self.pwd_page = self.pages - 2

    @property
    def uid(self) -> bytes:
        m = self.memory
        return bytes(m[0:3] + m[4:8])

    def read(self, page: int) -> bytes:
        out = bytearray()
        for i in range(4):
            p = (page + i) % self.pages
            out += b"\0\0\0\0" if p >= self.pwd_page else self.memory[p * 4:p * 4 + 4]  # PWD/PACK read as zeros
        return bytes(out)

    def load_table(self, bridge: NfcBridge):
        bridge.table_clear()
        bridge.table_set(b"\x60", NTAG_VERSIONS[self.pages])
        bridge.table_set(b"\x3C\x00", self.signature)
        for page in range(self.pages):
            bridge.table_set(bytes([0x30, page]), self.read(page))

    def handle(self, bridge: NfcBridge, frame: Frame):
        d = frame.data
        if not frame.crc_ok or not d:
            return bridge.nak(frame, 0x1)
        cmd = d[0]
        if cmd == 0x3A and len(d) == 3 and d[1] <= d[2] < self.pages:
            data = b"".join(self.read(p)[:4] for p in range(d[1], d[2] + 1))
            return bridge.answer(frame, data)
        if cmd == 0x1B and len(d) == 5:
            pack = bytes(self.memory[(self.pwd_page + 1) * 4:(self.pwd_page + 1) * 4 + 2])
            return bridge.answer(frame, pack if pack != b"\0\0" else b"\x80\x80")
        if cmd == 0x39 and len(d) == 2 and d[1] == 0x02:
            return bridge.answer(frame, b"\0\0\0")
        if cmd in (0xA2, 0xA0) and len(d) == 6 and 2 <= d[1] < self.pages:
            page = d[1]
            self.memory[page * 4:page * 4 + 4] = d[2:6]
            bridge.ack(frame)
            for p in range(max(0, page - 3), page + 1):  # keep the READ answers current
                bridge.table_set(bytes([0x30, p]), self.read(p))
            return
        return bridge.nak(frame, 0x0)


def _hex(b: bytes) -> str:
    return b.hex(" ").upper()


def cmd_info(bridge: NfcBridge, args):
    h = bridge.hello
    caps = [name for i, name in enumerate(CAPABILITIES) if h["capabilities"] & (1 << i)]
    print(f"{h['firmware']} (protocol {h['version']}, max payload {h['max_payload']})")
    print("capabilities:", ", ".join(caps))
    print("status:", bridge.status())


def cmd_read_a(bridge: NfcBridge, args):
    bridge.reader(TECH_A)
    uid, atqa, sak = bridge.activate_a()
    print(f"UID {_hex(uid)}  ATQA {_hex(atqa)}  SAK {sak:02X}")
    if sak == 0x00:
        version = bridge.transceive(b"\x60", add_crc=True, check_crc=True)
        if version.ok:
            print("GET_VERSION", _hex(version.data))
        for page in range(0, args.pages, 4):
            r = bridge.transceive(bytes([0x30, page]), add_crc=True, check_crc=True)
            if not r.ok or not r.flags & FRAME_CRC_OK:
                if r.bits == 4:
                    print(f"page {page:02X}: NAK {r.data[0]:X}")
                break
            for i in range(4):
                print(f"page {page + i:02X}: {_hex(r.data[i * 4:i * 4 + 4])}")
    bridge.stop()


def cmd_emulate_ntag(bridge: NfcBridge, args):
    with open(args.dump, "rb") as f:
        raw = f.read()
    size = next((s for s in (924, 540, 180) if len(raw) >= s), None)
    if size is None:
        raise SystemExit("dump too small for an NTAG213")
    tag = NtagEmulator(bytearray(raw[:size]), raw[size:size + 32].ljust(32, b"\0"))
    tag.load_table(bridge)
    bridge.on_frame = lambda frame: tag.handle(bridge, frame)  # answer on the read thread
    bridge.listen_a(tag.uid)
    print(f"Emulating UID {_hex(tag.uid)}; hold the Flipper to a reader. Ctrl+C to stop.")
    names = {FIELD_ON: "field on", FIELD_OFF: "field off", ACTIVATED: "selected", HALTED: "HLTA"}
    try:
        for event in bridge.events():
            if event.type == FRAME:
                f = event.frame
                print(f"{f.timestamp_us / 1000:10.3f}  R> {_hex(f.data):<30} {'(table)' if f.from_table else ''}")
            elif event.type in names:
                print(f"{event.timestamp_us / 1000:10.3f}  -- {names[event.type]}")
            else:
                print("   log:", event.text)
    except KeyboardInterrupt:
        pass
    print("status:", bridge.status())
    bridge.stop()


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("info", help="show firmware, capabilities and counters")
    read = sub.add_parser("read-a", help="activate a type A card (and read NTAG pages)")
    read.add_argument("--pages", type=int, default=16, help="pages to read from NTAG/Ultralight cards")
    emulate = sub.add_parser("emulate-ntag", help="emulate an NTAG213/215/216 from a dump")
    emulate.add_argument("dump")
    args = parser.parse_args()

    with NfcBridge.open() as bridge:
        {"info": cmd_info, "read-a": cmd_read_a, "emulate-ntag": cmd_emulate_ntag}[args.command](bridge, args)


if __name__ == "__main__":
    try:
        main()
    except BridgeError as ex:
        sys.exit(f"error: {ex}")
