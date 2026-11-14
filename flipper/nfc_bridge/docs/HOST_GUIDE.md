# Writing a host for the NFC Bridge

This guide covers how to find and open the Flipper from each platform, how to keep latency low, and what each mode is good for. The message formats are in [PROTOCOL.md](PROTOCOL.md).

A host to start from:

- **Python:** [`examples/nfc_bridge.py`](../examples/nfc_bridge.py) is a single-file library with a command line (info, reading type A tags, emulating an NTAG21x from a dump). It needs `pip install hidapi`.

## Finding the device

Match all of these, because the VID/PID pair is a shared test ID:

- VID `0x1209` and PID `0x0001`.
- Usage page `0xFF41` (usage `0x01`), or the product string `NFC Bridge`.

The device appears when the app starts on the Flipper and disappears when it exits, so watch for hotplug rather than enumerating once.

| Platform | API | Notes |
|---|---|---|
| Windows | HID class driver (`hid.dll`, `CreateFile` + `WriteFile`/`ReadFile`, or hidapi / HidSharp) | No driver to install. Writes and reads carry a leading report ID byte (`0`), so buffers are 65 bytes. |
| macOS | IOKit `IOHIDManager` (or hidapi / HidSharp) | Sandboxed apps need the `com.apple.security.device.usb` entitlement. |
| Linux | `hidraw` (hidapi's hidraw backend) | Install the udev rule below for access without root. |
| Browsers (Chrome, Edge) | WebHID | See the snippet below; needs HTTPS or localhost. |
| Android | USB Host API (`UsbManager`, interrupt transfers) | Talk to the endpoints directly; HID descriptors aren't parsed for you. |

### Linux udev rule

Save as `/etc/udev/rules.d/70-nfc-bridge.rules` (also in [`examples/70-nfc-bridge.rules`](../examples/70-nfc-bridge.rules)), then run `sudo udevadm control --reload` and replug:

```
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="1209", ATTRS{idProduct}=="0001", MODE="0660", TAG+="uaccess"
```

### WebHID

```js
const [device] = await navigator.hid.requestDevice({
  filters: [{ vendorId: 0x1209, productId: 0x0001, usagePage: 0xff41 }],
});
await device.open();
device.addEventListener("inputreport", (e) => {
  const report = new Uint8Array(e.data.buffer); // 64 bytes, no report ID
  // feed into your chunk assembler
});
await device.sendReport(0, report64); // report ID 0, 64-byte Uint8Array
```

## Latency

Card emulation is a race against the reader's timeout. Each relayed frame costs:

| Cost | Typical time |
|---|---|
| Flipper → host | Up to 1 ms of USB polling, plus OS scheduling |
| Host computes the answer | Hopefully microseconds |
| Host → Flipper | Up to 1 ms of USB polling |
| Flipper transmits | Immediately |

Expect about 2–4 ms in total. To keep it there:

- **Read on a dedicated thread** that blocks on the HID read, and answer from that thread. Thread pool hops and UI dispatching add milliseconds.
- **Precompute answers** and keep tag state in memory. Never do I/O, logging to disk, or locking against the UI on the answer path. Queue the log and write it elsewhere.
- **Put static answers in the answer table** (READs of read-only memory, GET_VERSION, READ_SIG, select responses of ISO 14443-4 apps). Table hits cost a few µs, and with NotifyTableHits you still see them in your log.
- **Watch STATUS:** `timeouts` counts frames whose answer took longer than the LISTEN timeout, `late` counts answers that arrived after that, and `worst round trip` is the slowest relayed frame.
- **On Windows, a stable 1 ms poll** helps more than anything else. Avoid USB hubs that share a slow transaction translator, and keep the PC from throttling.

### What can be relayed

| Technology | Relay over USB? |
|---|---|
| ISO 14443-A, after SELECT (NTAG, Ultralight, MIFARE Classic, 14443-4) | Usually. ISO 14443-3 only sets a minimum answer delay; the reader's timeout is the limit. NTAG readers typically wait 5 ms or more. ISO 14443-4 cards can advertise up to about 5 s (FWI 14 in the ATS) and request more with S(WTX). |
| ISO 14443-A anticollision | Never needed: done in hardware with the LISTEN parameters. |
| FeliCa | The Flipper answers polling. Readers derive the response time they allow for other commands from the PMm the card advertises, so pick a generous PMm. |
| ISO 15693 | No. A tag must answer about 320 µs after the reader's frame, within a few µs, so only answer table hits are fast enough. Preload every response the reader will ask for (inventory, the blocks it reads, system info). |
| ISO 14443-B | Card emulation isn't available. Reader mode works. |

Reader mode has no timing problem: the host drives the exchange, and the card waits for the reader.

## Patterns

### Emulating a card

1. Send LISTEN with StripCrc, plus HandleHlta for type A.
2. Fill the answer table with whatever never changes.
3. For every FRAME without FromTable, compute the answer and send ANSWER with the same sequence number.
4. On FIELD_OFF, reset the card's session state (authentication, selected sector). On HALTED, the card is halted until the next selection, which the Flipper handles.

### Sniffing what a reader asks for

Emulate the card you want to observe and log every FRAME. For a reader that needs real answers, put a real card's responses in the table or answer from a model of it.


### Reading cards

1. Send READER with the technology.
2. Drive the protocol with TRANSCEIVE. For type A: WUPA, then SDD and SELECT per cascade level, then commands with AddCrc and CheckCrc.
3. Use FIELD_RESET between cards, or to recover a card that stopped responding.

`examples/nfc_bridge.py` implements type A activation (`activate_a`) as a reference.

### MIFARE Classic and other custom-parity protocols

- **Emulation:** set ReportParity, and answer with CustomParity plus the encrypted parity bits.
- **Reading:** use the CustomParity kind; the result carries the received parity.

The bridge does no Crypto-1 itself.

## Robustness

- HELLO first, check the version, and refuse anything else.
- Every control message is acknowledged. Wait for the ACK (with a timeout) before relying on the new mode.
- A HID read error means the Flipper app exited or the cable was pulled. The Flipper stops emulating when USB disconnects, so the host doesn't need to clean up.
- Messages are never retried at the protocol level. A lost ANSWER means one silent frame; readers retry on their own.
