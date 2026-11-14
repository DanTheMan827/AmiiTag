# NFC Bridge for Flipper Zero

A Flipper Zero app that turns the Flipper's NFC front end into a USB peripheral for a computer. It doesn't understand any NFC protocol itself: the host decides every answer or command. Use it as a card emulator, a reader, or a sniffer of what readers ask for, in any project.

| | Card emulation (listen) | Reader |
|---|---|---|
| ISO 14443-A (NTAG, Ultralight, MIFARE Classic, 14443-4 / DESFire, ...) | ✓ (anticollision in hardware, then raw frames) | ✓ (short frames, anticollision frames, custom parity) |
| ISO 14443-B | not possible with the Flipper's NFC chip driver | ✓ |
| ISO 15693 (NFC-V) | ✓ (answer table only; see timing) | ✓ |
| FeliCa (NFC-F) | ✓ (the Flipper answers polling with your IDm/PMm) | ✓ |

## How it works

The app runs on the firmware's technology-level `Nfc` API, one layer below the protocol listeners and pollers the built-in NFC app uses.

**Listen mode:**

1. The Flipper is a card. For type A, the ST25R3916 handles REQA/WUPA, anticollision and SELECT in hardware with the UID, ATQA and SAK you choose. HLTA can optionally be handled on the Flipper.
2. Every other frame the reader sends goes to the host as a FRAME event. The host's ANSWER (data with or without CRC, a 4-bit ACK/NAK, silence, sleep, custom parity) is transmitted at once.
3. An **answer table** lets the Flipper answer known frames by itself in microseconds. It's for readers too impatient for a USB round trip, and for ISO 15693.

**Reader mode:** the Flipper is a reader. The host sends TRANSCEIVE requests (any bit length, optional CRC, custom parity, type A short and anticollision frames) and gets each response with its timing.

**Transport:** a vendor-defined USB HID interface with 64-byte reports and 1 ms interrupt endpoints. No driver is needed on Windows, macOS or Linux, and it works from browsers through WebHID. While the app runs, it replaces the Flipper's USB serial port, so qFlipper and the CLI come back when you exit.

## Documentation

- [docs/PROTOCOL.md](docs/PROTOCOL.md): the wire protocol (messages, flags, framing, example sessions).
- [docs/HOST_GUIDE.md](docs/HOST_GUIDE.md): opening the device on each OS and in the browser, latency, what can be relayed, patterns for emulation, sniffing and reading.
- [examples/nfc_bridge.py](examples/nfc_bridge.py): a Python host library and command line (`info`, `read-a`, `emulate-ntag`).
- [examples/70-nfc-bridge.rules](examples/70-nfc-bridge.rules): the Linux udev rule.

## Build and install

Use [ufbt](https://github.com/flipperdevices/flipperzero-ufbt):

```sh
pip install ufbt
cd flipper/nfc_bridge
ufbt update            # SDK for your firmware: --channel=release|rc|dev, or --index-url for custom firmware
ufbt                   # builds dist/nfc_bridge.fap
ufbt launch            # installs over USB and starts it (or copy the .fap to SD:/apps/USB/)
```

The app needs firmware with the current `Nfc` API (`nfc_start`, `nfc_listener_tx`, `nfc_iso14443a_*`), which official firmware has had since the NFC refactor in 0.98.

FeliCa card emulation needs `nfc_felica_listener_set_sensf_res_data`. On older firmware, comment out `BRIDGE_HAS_FELICA_LISTENER` in `bridge_nfc.c`; HELLO then stops advertising listen F.

| File | Contents |
|---|---|
| `nfc_bridge.c` | App entry, USB message pump, control dispatch, screen |
| `bridge_nfc.c/.h` | Listen and reader engine on the `Nfc` API |
| `answer_table.c/.h` | Local exact-match answers |
| `bridge_crc.c/.h` | CRC_A, ISO 13239 (B/V) and FeliCa CRCs |
| `usb_bridge.c/.h` | The vendor HID USB interface (libusb_stm32 under `FuriHalUsbInterface`) |
| `bridge_protocol.h` | Protocol constants shared with hosts |

## Use

1. Plug the Flipper into the computer and open **Apps > USB > NFC Bridge**. The screen says "USB ready".
2. Start a host, such as the Python example:
   ```sh
   pip install hidapi
   python examples/nfc_bridge.py info
   python examples/nfc_bridge.py read-a --pages 16
   python examples/nfc_bridge.py emulate-ntag my_tag.bin
   ```
3. The Flipper shows the mode, technology, field state, counters and the last frame.
4. **Back** exits the app and restores the normal USB interface.

## Timing in one paragraph

Relaying a frame costs one USB round trip, about 2–4 ms. That is fine for ISO 14443-A after selection, where readers usually wait 5 ms or more and 14443-4 cards can ask for seconds, and for FeliCa with a generous PMm. It is far too slow for ISO 15693, which must answer within about 320 µs, so preload those answers into the table. Reader mode has no timing constraints. The host guide has the details and tips.

## Limitations

- **Single-card readers.** Reader-mode activation in the examples assumes one card in the field; collision resolution is up to the host, using SDD frames.
- **No ISO 14443-B card emulation**, since the firmware's NFC chip driver has no listener for it.
- **No crypto on the Flipper.** For MIFARE Classic, the host does Crypto-1 and the bridge only carries the parity bits.
- **Shared test USB ID.** The VID/PID is pid.codes' test ID 1209:0001. Hosts tell the bridge apart by usage page `0xFF41` or the product string.
