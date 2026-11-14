# NFC Bridge protocol, version 2

This is the wire protocol between the Flipper Zero NFC Bridge app and a host computer. `bridge_protocol.h` is the source of truth for the numbers, and this document explains them.

## Transport

The Flipper switches its USB port to a vendor-defined HID device while the app runs.

| | |
|---|---|
| VID / PID | `0x1209` / `0x0001` (the shared pid.codes test PID, so also check the next three fields) |
| Product string | `NFC Bridge` |
| Usage page / usage | `0xFF41` / `0x01` |
| Reports | 64-byte input and output reports with no report ID |
| Endpoints | Interrupt IN `0x81` and OUT `0x01`, polled every 1 ms |

No driver is needed on Windows, macOS or Linux. Linux needs a udev rule for access without root (see [HOST_GUIDE.md](HOST_GUIDE.md)). Most HID APIs want a leading report ID byte of `0` when writing, and some return it when reading.

## Framing

Every report is one chunk of a message:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | Message type |
| 1 | 1 | Sequence number |
| 2 | 1 | Chunk index, 0-based |
| 3 | 1 | Chunk count (1 for messages up to 58 bytes) |
| 4 | 2 | Total payload length, little endian, at most 1024 |
| 6 | 58 | Payload bytes of this chunk, zero-padded |

- Chunks of a message are sent back to back.
- A receiver that sees an unexpected chunk index drops the message.
- Empty messages are one chunk with length 0.

The sequence number is meaningful only for two pairs:

- **FRAME → ANSWER.** The device numbers frames; the host copies the number into its ANSWER.
- **TRANSCEIVE → TRANSCEIVE_RESULT.** The host chooses the number and the device copies it.

Other messages use 0.

## Conventions

- **Integers** are little endian.
- **Bit counts** give exact frame lengths. Data holds `ceil(bits / 8)` bytes. The first bit on air is bit 0 of byte 0, so a 4-bit ACK is `bits = 4, data = [0x0A]`, and a 7-bit REQA would be `bits = 7, data = [0x26]`.
- **Parity bitmaps** (ISO 14443-A only) follow the data and hold one bit per data byte, LSB first: the parity of byte *i* is `(parity[i / 8] >> (i % 8)) & 1`.
- **Timestamps** are the Flipper's µs clock. It starts when the app starts and wraps after about 71 minutes.
- **Technologies:** `0` ISO 14443-A, `1` ISO 14443-B, `2` ISO 15693 (NFC-V), `3` FeliCa (NFC-F).
- **CRCs:**
  - Type A: CRC_A (initial `0x6363`, LSB first).
  - Types B and V: the ISO/IEC 13239 CRC (initial `0xFFFF`, inverted, LSB first).
  - FeliCa: CRC-16/CCITT (initial `0`, MSB first), computed over the length byte and the data.

## Messages, host → device

| Type | Name | Payload | Reply |
|---|---|---|---|
| `0x81` | HOST_HELLO | none | HELLO |
| `0x82` | LISTEN | `u8 tech, u8 listen flags, u16 answer timeout ms, tech parameters` | ACK |
| `0x83` | READER | `u8 tech, u8 0` | ACK |
| `0x84` | STOP | none | ACK |
| `0x85` | GET_STATUS | none | STATUS |
| `0x86` | SET_TIMING | `u32 fdt poll fc, u32 fdt listen fc, u32 guard µs, u32 poll-poll µs, u32 mask receive fc` | ACK |
| `0x88` | TRANSCEIVE | `u8 kind, u8 tx flags, u16 bits, u32 timeout µs, data, [parity]` | TRANSCEIVE_RESULT |
| `0x89` | FIELD_RESET | none | ACK |
| `0x8A` | TABLE_CLEAR | none | ACK |
| `0x8B` | TABLE_SET | `u8 tx flags, u8 match length, u16 answer bits, match, answer, [parity]` | ACK |
| `0x8C` | TABLE_REMOVE | `u8 match length, match` | ACK |
| `0x90` | ANSWER | `u8 tx flags, u16 bits, data, [parity]`, with the FRAME's sequence number | none |

### LISTEN: card emulation

The Flipper becomes a card, and LISTEN replaces whatever mode was running.

**Answer timeout (ms):** how long the Flipper waits for an ANSWER before staying silent. 0 means 25 ms.

**Technology parameters:**

| Tech | Parameters |
|---|---|
| A (0) | `u8 uid length (4, 7 or 10), uid, u8 atqa[2] in air order (NTAG: 44 00), u8 sak` |
| V (2) | none (inventory and every other command are frames) |
| F (3) | `u8 idm[8], u8 pmm[8], u16 system code` (needs FeliCa card emulation in the firmware) |
| B (1) | not supported by the Flipper's NFC chip driver: ACK `Unsupported` |

**Listen flags:**

| Bit | Name | Meaning |
|---|---|---|
| 0 | StripCrc | Check the CRC of received frames. When it is valid, remove it and set FRAME flag CrcOk. Invalid frames arrive whole without the flag. |
| 1 | HandleHlta | Type A: answer HLTA (`50 00` + CRC) on the Flipper by going to sleep, and send HALTED instead of a FRAME. |
| 2 | NotifyTableHits | Also report frames the answer table answered (FRAME flag FromTable; don't answer them). |
| 3 | ReportParity | Type A: include the received parity bits. |

For type A, the ST25R3916 performs REQA/WUPA, anticollision and SELECT in hardware with the configured UID, ATQA and SAK, then sends ACTIVATED. Every later frame arrives as a FRAME, unless the answer table or HandleHlta deals with it.

### ANSWER

ANSWER responds to the FRAME with the same sequence number. An ANSWER that comes after the timeout, or that answers an older frame, is counted as late and dropped.

**Tx flags** (used by ANSWER, TABLE_SET and TRANSCEIVE):

| Bit | Name | Meaning |
|---|---|---|
| 0 | AddCrc | Append the technology's CRC (whole-byte frames only). |
| 1 | Sleep | Listen: go to sleep after sending. Type A then ignores everything but WUPA (an NTAG does this after a NAK). |
| 2 | CustomParity | Type A: send the parity bits that follow the data instead of odd parity (MIFARE Classic Crypto-1). |
| 3 | CheckCrc | TRANSCEIVE only: check the response's CRC; when valid, strip it and set CrcOk. |

**Common answers:**

| Answer | Encoding |
|---|---|
| Stay silent | `bits = 0` |
| Silent, then sleep | `bits = 0`, flag Sleep |
| Data with CRC | `bits = 8n`, flag AddCrc |
| 4-bit ACK (NTAG) | `bits = 4, data = 0A` |
| 4-bit NAK, then idle (NTAG) | `bits = 4, data = 00`, flag Sleep |

### TRANSCEIVE: reader mode

TRANSCEIVE only works after READER. The field turns on with READER. Requests run one at a time on the NFC worker thread, and each gets exactly one TRANSCEIVE_RESULT.

| Kind | Name | Notes |
|---|---|---|
| 0 | Frame | Any bit length; AddCrc / CheckCrc as wanted. |
| 1 | REQA | Type A short frame `26`; data ignored. |
| 2 | WUPA | Type A short frame `52`; data ignored. |
| 3 | SDD | Type A anticollision frame (bit-oriented; e.g. `93 20` with bits 16). |
| 4 | CustomParity | Type A frame sent with the host's parity bits; the result includes the received parity. |

- **Timeout:** the frame waiting time in µs (0 means 5000).
- **FIELD_RESET:** turns the field off and back on with the firmware's reset pause, which forces cards back to IDLE.

### SET_TIMING

Overrides the Nfc layer timings from the next LISTEN or READER. A 0 keeps the default.

- **fc** means 13.56 MHz carrier cycles (1 fc ≈ 73.7 ns).
- **Reader defaults:** they match the firmware's own pollers.

| Tech | fdt poll | guard | poll-poll |
|---|---|---|---|
| A | 1620 fc | 5000 µs | 1100 µs |
| B | 9000 fc | 5000 µs | 1100 µs |
| V | 4202 fc | 5000 µs | 1500 µs |
| F | 10000 fc | 20000 µs | 1100 µs |

- **Listen defaults:** the firmware's own.

### Answer table

The device answers matching frames on its own, within microseconds, without asking the host.

- **Matching:** a frame matches an entry when its bytes, after CRC stripping if StripCrc is set, are exactly equal to the entry's match.
- **Sending:** the answer is sent with the entry's tx flags, so AddCrc and Sleep work there too.
- **Replacing:** TABLE_SET with an existing match replaces that entry.
- **Size:** the table holds 16 KB, including 4 bytes of overhead per entry. TABLE_SET replies `TableFull` when it doesn't fit.
- **Lifetime:** the table survives LISTEN, READER and STOP; only TABLE_CLEAR empties it.

The host keeps the table consistent with its state. For example, after it handles a WRITE, it updates the READ answers that cover the written page.

## Messages, device → host

| Type | Name | Payload |
|---|---|---|
| `0x01` | HELLO | `u8 version (2), u8 0, u16 max payload, u32 capabilities, ASCII firmware name` |
| `0x02` | ACK | `u8 acknowledged type, u8 status` |
| `0x10` | FIELD_ON | `u32 timestamp` |
| `0x11` | FIELD_OFF | `u32 timestamp` |
| `0x12` | ACTIVATED | `u32 timestamp`: anticollision and SELECT done (A), or polled (F) |
| `0x13` | HALTED | `u32 timestamp`: HLTA handled (listen A with HandleHlta) |
| `0x20` | FRAME | `u32 timestamp, u8 frame flags, u16 bits, data, [parity]` |
| `0x21` | TRANSCEIVE_RESULT | `u8 error, u8 frame flags, u16 bits, u32 elapsed µs, u8 0, data, [parity]` |
| `0x30` | STATUS | `u32 frames, u32 answered, u32 timeouts, u32 late, u32 worst round trip µs, u32 table hits, u32 transceives` |
| `0x7F` | LOG | ASCII text |

**ACK status:** 0 Ok, 1 BadArguments, 2 Unsupported, 3 WrongMode, 4 TableFull, 5 NotFound.

**Capabilities** (HELLO):

| Bit | Capability |
|---|---|
| 0–3 | Listen A, B, V, F |
| 4–7 | Reader A, B, V, F |
| 8 | Answer table |
| 9 | Custom parity |

**Frame flags:**

| Bit | Name | Meaning |
|---|---|---|
| 0 | CrcOk | A valid CRC was checked and removed. |
| 1 | Parity | A parity bitmap follows the data. |
| 2 | FromTable | Already answered from the answer table; no ANSWER expected. |

**TRANSCEIVE_RESULT errors:** 0 None, 1 Timeout (no response), 2 IncompleteFrame, 3 DataFormat (bad framing or parity), 4 Internal, 5 WrongMode (not in reader mode), 6 BadArguments.

The data of a TRANSCEIVE_RESULT starts at payload offset 9 (byte 8 is reserved).

## Typical sessions

**Emulating an NTAG215:**

```
host  HOST_HELLO                          device HELLO v2
host  TABLE_CLEAR, TABLE_SET x 136         device ACK ... (READ 00..86 and GET_VERSION answered locally)
host  LISTEN tech=A flags=StripCrc|HandleHlta|NotifyTableHits uid=04..(7) atqa=44 00 sak=00
                                           device ACK
                                           device FIELD_ON, ACTIVATED
                                           device FRAME seq=0 flags=CrcOk|FromTable 30 04   (already answered)
                                           device FRAME seq=7 flags=CrcOk 1B xx xx xx xx    (PWD_AUTH)
host  ANSWER seq=7 flags=AddCrc bits=16 80 80
                                           device HALTED, FIELD_OFF
```

**Reading a type A tag's UID:**

```
host READER tech=A                         device ACK
host TRANSCEIVE seq=1 kind=WUPA            device RESULT seq=1 bits=16 44 00          (ATQA)
host TRANSCEIVE seq=2 kind=SDD bits=16 93 20
                                           device RESULT seq=2 bits=40 88 04 A1 B2 9F (CT, UID0-2, BCC)
host TRANSCEIVE seq=3 Frame AddCrc|CheckCrc 93 70 88 04 A1 B2 9F
                                           device RESULT seq=3 CrcOk 04                (SAK: UID incomplete)
...cascade level 2 with 95 20 / 95 70...
```

## Versioning

- The version is in HELLO. Hosts should refuse versions they don't know.
- New message types or flags that old hosts can ignore keep version 2; anything that changes existing layouts bumps it.
