# PN532 UART NFC provider

`AmiiTag.Nfc.Pn532` is a separate .NET 10 project implementing the same `INfcProvider` and raw `INfcTagConnection` interfaces as the MFRC522 provider. It references only `AmiiTag.Nfc`.

## Connecting

1. Configure the PN532 module's switches/jumpers for **HSU / UART** before powering it on. Consult the board's labels/manual for its switch positions and supply voltage.
2. Connect its TTL UART to a USB serial adapter: TX to RX, RX to TX, and common ground. Use the board's specified UART logic levels.
3. Under **Settings → NFC Reader**, select **PN532 (UART / serial / COM port)** and the appropriate port. In the browser, use **Choose Serial Port...** to grant access.

The driver uses the PN532's default **115200 baud, 8 data bits, no parity, 1 stop bit**. If another application changed its baud rate, power-cycle the module first. The MFRC522 speed setting does not change the PN532 rate. Supported hosts match the existing MFRC522 serial integration: Windows COM ports and Web Serial in desktop Chrome/Edge.

The existing provider and port preferences remember the selection; no additional preferences are needed. When switching reader types, the application releases the previous provider's serial port.

## Device detection investigation

The documented identity queries do not provide a mutually harmless automatic probe:

| Probe | Intended reader | Effect on the other protocol at matching baud |
| --- | --- | --- |
| `B7` | MFRC522 VersionReg read, address `37` | PN532 ignores bytes before its `00 FF` synchronization pattern; `B7` does not form a command. |
| `00 00 FF 02 FE D4 02 2A 00` | PN532 GetFirmwareVersion | Decoding these bytes as MFRC522 UART includes writes to register `02` (ComIEn), so this is not a read-only probe of an MFRC522. |

The first probe can positively identify an answering MFRC522, but silence does not identify a PN532. A different baud rate, an unplugged/reset chip, an unknown compatible version, or a transport failure also causes silence. Wrong-baud decoding adds further uncertainty. Consequently, the app keeps reader type selection explicit instead of sending PN532 initialization traffic after an inconclusive MFRC522 read.

References: [MFRC522 datasheet, section 8.1.3.3](https://www.nxp.com/docs/en/data-sheet/MFRC522.pdf); [PN532 UM0701-02, sections 6.2.1.6 and 7.2.2](https://www.nxp.com/docs/en/user-guide/141520.pdf).

## Implementation

- Drains old input, sends the C106 long HSU wake preamble together with SAMConfiguration, enters SAM normal mode and checks the PN532 firmware identity and ISO14443-A support.
- Reapplies configuration on each connection to recover from module resets while its USB adapter remains connected.
- Bounds passive-target polling, disables automatic ISO14443-4 RATS, and disables RF command retries so writes cannot be automatically repeated.
- Uses InCommunicateThru for raw NFC-A commands, with PN532-generated transmit CRC_A, host receive CRC_A validation, and 4-bit ACK/NAK handling. Supports reads, writes, authentication and no-response commands through the existing NFC clients.
- Supports normal and extended UART frames with checksum, response-command and length validation. Advertises 63 pages per FAST_READ (252 data bytes).
- Supports temporary RF response timeouts and reselects the stored target using InDeselect/InSelect without explicitly cycling the RF field.
- Serializes lifecycle and RF operations. Interrupted/malformed UART exchanges close the stream; the next connection opens and initializes it again. Stale tag connections cannot operate on a newer session.
- Releases the RF field and port when the provider is released.

Serial transport abstractions moved from `AmiiTag.Nfc.Mfrc522/SerialAbstractions.cs` to the shared `AmiiTag.Nfc` project and namespace. Both drivers and the Windows/browser adapters use these shared interfaces. External consumers referring to `AmiiTag.Nfc.Mfrc522.ISerialPortFactory` or its related interfaces must update their namespace to `AmiiTag.Nfc`.

## Validation

`AmiiTag.Tests/Pn532Tests.cs` contains 39 focused xUnit tests covering documented firmware framing, fragmented/extended frames, invalid checksums and replies, NACKs, host timeouts, cancellation, initialization, tag polling/UIDs, raw reads/writes, extended FAST_READ, ACK/NAK and RF CRC handling, passive acknowledgements, temporary RF timeouts, reselecting, release/reopen, stale sessions, invalid firmware, and browser prompt restrictions.

All 39 PN532/read-route tests and three serial-lifecycle regression tests passed using a standalone .NET 10 runner. The additional tests exercise C106 LowVbat startup, a chip reset without USB disconnect, ACK/response timeout diagnostics, and temporary MFRC522 port cleanup after cancellation or I/O failure. The MFRC522 and PN532 driver sources also compiled together against the shared serial interfaces with warnings treated as errors. The normal NuGet/MSBuild build and test flow was blocked by this execution environment's mutex/socket restrictions. Full Windows/WebAssembly application builds and physical PN532 hardware validation remain to be run on a development machine:

```sh
dotnet test AmiiTag.Tests/AmiiTag.Tests.csproj
```

## Startup and reader-switch correction

The follow-up `pn532-uart-fix.patch` applies on top of the initial PN532 patch. It addresses the supplied log's startup stage and the COM port ownership path:

- SAMConfiguration now carries `55 55` plus a long zero preamble and the command in one write, following [NXP AN10609_2 section 2.4.2](https://www.nxp.com/docs/en/nxp/application-notes/153710.pdf). The earlier separate wake/abort/drain sequence has been removed. A fresh wake preamble is sent for SAM on a reused port too, to handle a chip power reset.
- Temporary MFRC522 streams are disposed if initial draining, version probing, speed switching, or initialization fails before the provider retains its reader. This prevents cancelled MFRC522 detection from keeping the COM port locked when switching to PN532.
- Debug logging now identifies the selected port, UART speed, command, ACK receipt and response length. Timeout messages distinguish a missing ACK from a missing command response and report received byte counts. Tag contents and passwords are not logged.

Restart the application after applying this correction so any handle leaked by the old code is released. The existing log proves that initialization timed out, but does not establish whether the module sent an ACK, or rule out port selection, hardware mode, wiring, or baud-rate issues. These changes have been tested in simulation, not against the user's physical module.

## Reading a tag that stays on the reader

`pn532-held-tag-fix.patch` applies after the original PN532 patch and startup correction. The previous close path sent InDeselect (HALT), but subsequent discovery used InListPassiveTarget without InitiatorData, which issues REQA. A halted tag ignores REQA until it is woken or loses power, explaining why placing the tag after clicking Read worked while keeping it on the antenna did not.

The provider now remembers the last selected UID per port and supplies it, with the required cascade tags, to InListPassiveTarget. This makes the PN532 issue ALL_REQ/WUPA and select that tag again ([UM0701-02 section 7.3.5](https://www.nxp.com/docs/en/user-guide/141520.pdf)). The UID survives serial-port closure caused by a cancelled background poll. If the previous tag is gone, discovery falls back to finding a different tag. This does not explicitly cycle the RF field.

The accompanying application changes reuse the version already detected by ReadAnyAsync and allow up to three attempts for NTAG215 version/memory/signature reads, reselecting the same tag between attempts. Cancellation, lost UART connections, unsupported commands, and failed reselects stop recovery; write/authentication operations are not added to this retry policy. The v3 sector-aware recovery remains separate. PN532 logging now includes the NFC opcode, RF status, and response length, without dumping tag payloads or passwords.

Additional tests cover held-tag reconnection, cancellation while background discovery is running, swapping tags, 4/7/10-byte UID encoding, a single version query, recovery from RF protocol/CRC failures, bounded retries, lost connections, failed reselection, cancellation, and write non-replay. The standalone harness compiles the actual read-route method bodies with lightweight dump containers, excluding crypto/write implementations. All 42 focused checks pass. Full platform builds and verification with the physical reader remain outstanding.

## CRC mode for firmware-generated control frames

`pn532-crc-mode-fix.patch` applies after the held-tag fix. The supplied Flipper air capture showed `50 00` arriving as only 16 bits without a CRC. That is a malformed HALT frame: with CRC_A it must be `50 00 57 CD`. The previous raw setup disabled the PN532 transmit CRC generator and manually appended CRC to host-issued raw commands. However, that same chip setting also affected HALT and selection frames generated internally by the PN532 firmware, to which the host cannot append the missing CRC.

Transmit CRC generation now stays enabled. InCommunicateThru receives bare NFC command bytes so that CRC is appended exactly once by the chip. Receive CRC checking remains disabled during raw exchanges to allow 4-bit ACK/NAK replies; normal replies still undergo host CRC validation. Before firmware selection/deselection, the driver restores normal transmit/receive CRC and byte-framing settings, then restores the raw receive mode after selection. This does not explicitly switch off the RF field.

The UART/tag simulation now models transmit CRC generation and the firmware's HALT frame, instead of assuming every deselection successfully halts the tag. Three additional tests check the exact HALT wire frame, a single transmit CRC on raw reads, and restoration of CRC/framing modes around reselection. All 42 focused checks pass, and both driver source sets compile with warnings treated as errors. A new physical air capture is still needed to confirm the correction on the reader.

The N2 emulation capture also contains silent PREPARE_UNLOCK and UNLOCK operations. PREPARE_UNLOCK is intentionally silent in the model. UNLOCK requires the emulator's ButtonPressed state to be true after preparation; the air capture does not record that checkbox state, so silence by itself cannot establish a transport error in that sequence.
