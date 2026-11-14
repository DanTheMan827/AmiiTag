#pragma once

/*
 * NFC bridge protocol, version 2. Full description: docs/PROTOCOL.md.
 *
 * The Flipper exposes its NFC front end at the technology level (the firmware's Nfc layer) to a host over USB, on
 * either of two interfaces carrying the same 64 byte packets ("reports"); no driver needs installing anywhere:
 *   - a vendor defined HID interface (usage page 0xFF41): 1 ms interrupt endpoints, one packet per millisecond.
 *   - a vendor bulk interface (endpoints 0x82 in, 0x02 out): many packets per millisecond. Windows binds WinUSB to
 *     it through Microsoft OS 2.0 descriptors (device interface GUID BRIDGE_WINUSB_GUID).
 * The Flipper answers on the interface the host last sent on.
 *
 * Every packet is one chunk of a message:
 *
 *   [0]    message type
 *   [1]    sequence number (pairs FRAME/ANSWER and TRANSCEIVE/TRANSCEIVE_RESULT)
 *   [2]    chunk index (0 based)
 *   [3]    chunk count
 *   [4..5] total payload length, little endian
 *   [6..]  up to 58 payload bytes
 *
 * Multi-byte fields are little endian. Bit counts give the exact frame length; data holds ceil(bits / 8) bytes, the
 * first bit on air being bit 0 of byte 0. Parity bitmaps (ISO 14443-A only) hold one bit per data byte, LSB first.
 */

#include <stdint.h>

#define BRIDGE_PROTOCOL_VERSION 2
#define BRIDGE_REPORT_SIZE 64
#define BRIDGE_HEADER_SIZE 6
#define BRIDGE_CHUNK_PAYLOAD (BRIDGE_REPORT_SIZE - BRIDGE_HEADER_SIZE)
#define BRIDGE_MAX_PAYLOAD 1024
#define BRIDGE_MAX_FRAME 900 /* bytes of NFC data in one message (leaves room for headers and parity) */

#define BRIDGE_USB_VID 0x1209 /* pid.codes */
#define BRIDGE_USB_PID 0x0001 /* pid.codes test PID; hosts also check the product string and usage page */
#define BRIDGE_HID_USAGE_PAGE 0xFF41
#define BRIDGE_HID_USAGE 0x01
#define BRIDGE_USB_PRODUCT "NFC Bridge"
/* Device interface GUID of the bulk interface under WinUSB (Windows hosts find the device by it). */
#define BRIDGE_WINUSB_GUID "{C4A1D7E2-5B3F-4E88-9D21-6F0A3B7C9E14}"
#define BRIDGE_BULK_EP_IN 0x82
#define BRIDGE_BULK_EP_OUT 0x02

typedef enum {
    /* Device -> host */
    BridgeMsgHello = 0x01, /* u8 version, u8 0, u16 max payload, u32 capabilities, ASCII firmware name */
    BridgeMsgAck = 0x02, /* u8 acknowledged message type, u8 BridgeStatus */
    BridgeMsgFieldOn = 0x10, /* u32 timestamp (µs) */
    BridgeMsgFieldOff = 0x11, /* u32 timestamp */
    BridgeMsgActivated = 0x12, /* u32 timestamp: anticollision/selection done (type A), polled (type F) */
    BridgeMsgHalted = 0x13, /* u32 timestamp: HLTA handled by the Flipper (listen A with BridgeListenHandleHlta) */
    BridgeMsgFrame = 0x20, /* seq; u32 timestamp, u8 BridgeFrameFlag, u16 bits, data, [parity] */
    BridgeMsgTransceiveResult = 0x21, /* seq; u8 BridgeNfcError, u8 BridgeFrameFlag, u16 bits, u32 µs, data, [parity] */
    BridgeMsgStatus = 0x30, /* 7 x u32: frames, answered, timeouts, late, worst µs, table hits, transceives */
    BridgeMsgLog = 0x7F, /* ASCII text */

    /* Host -> device */
    BridgeMsgHostHello = 0x81, /* (empty) -> HELLO */
    BridgeMsgListen = 0x82, /* u8 BridgeTech, u8 BridgeListenFlag, u16 answer timeout ms, tech parameters -> ACK */
    BridgeMsgReader = 0x83, /* u8 BridgeTech, u8 0 -> ACK; the field turns on */
    BridgeMsgStop = 0x84, /* -> ACK */
    BridgeMsgGetStatus = 0x85, /* -> STATUS */
    BridgeMsgSetTiming = 0x86, /* 5 x u32 (0 = default): fdt poll fc, fdt listen fc, guard µs, poll-poll µs,
                                  mask receive fc. Applies from the next LISTEN/READER -> ACK */
    BridgeMsgTransceive = 0x88, /* seq; u8 BridgeTransceiveKind, u8 BridgeTxFlag, u16 bits, u32 timeout µs, data,
                                   [parity] -> TRANSCEIVE_RESULT */
    BridgeMsgFieldReset = 0x89, /* reader: field off, pause, field on -> ACK */
    BridgeMsgTableClear = 0x8A, /* -> ACK */
    BridgeMsgTableSet = 0x8B, /* u8 BridgeTxFlag, u8 match length, u16 answer bits, match, answer, [parity] -> ACK */
    BridgeMsgTableRemove = 0x8C, /* u8 match length, match -> ACK */
    BridgeMsgAnswer = 0x90, /* seq of the FRAME; u8 BridgeTxFlag, u16 bits (0 = stay silent), data, [parity] */
} BridgeMessageType;

typedef enum {
    BridgeTechIso14443a = 0,
    BridgeTechIso14443b = 1,
    BridgeTechIso15693 = 2,
    BridgeTechFelica = 3,
} BridgeTech;

typedef enum {
    BridgeStatusOk = 0,
    BridgeStatusBadArguments = 1,
    BridgeStatusUnsupported = 2,
    BridgeStatusWrongMode = 3,
    BridgeStatusTableFull = 4,
    BridgeStatusNotFound = 5,
} BridgeStatus;

typedef enum {
    BridgeCapListenA = 1 << 0,
    BridgeCapListenB = 1 << 1,
    BridgeCapListenV = 1 << 2,
    BridgeCapListenF = 1 << 3,
    BridgeCapReaderA = 1 << 4,
    BridgeCapReaderB = 1 << 5,
    BridgeCapReaderV = 1 << 6,
    BridgeCapReaderF = 1 << 7,
    BridgeCapAnswerTable = 1 << 8,
    BridgeCapCustomParity = 1 << 9,
} BridgeCapability;

typedef enum {
    /* Check the technology's CRC on received frames; when valid, strip it and set BridgeFrameCrcOk. */
    BridgeListenStripCrc = 1 << 0,
    /* Type A: answer HLTA (50 00 + CRC) on the Flipper by going to sleep, and report HALTED instead of a FRAME. */
    BridgeListenHandleHlta = 1 << 1,
    /* Report frames answered from the answer table (with BridgeFrameFromTable; no ANSWER expected). */
    BridgeListenNotifyTableHits = 1 << 2,
    /* Type A: include the received parity bits (for MIFARE Classic style protocols). */
    BridgeListenReportParity = 1 << 3,
} BridgeListenFlag;

typedef enum {
    BridgeFrameCrcOk = 1 << 0, /* a valid CRC was checked and removed */
    BridgeFrameParity = 1 << 1, /* a parity bitmap follows the data */
    BridgeFrameFromTable = 1 << 2, /* already answered from the answer table */
} BridgeFrameFlag;

typedef enum {
    BridgeTxAddCrc = 1 << 0, /* append the technology's CRC (whole bytes only) */
    BridgeTxSleep = 1 << 1, /* listen: go to sleep after sending (type A: idle until WUPA) */
    BridgeTxCustomParity = 1 << 2, /* type A: a parity bitmap follows the data */
    BridgeTxCheckCrc = 1 << 3, /* transceive: check and strip the CRC of the response */
} BridgeTxFlag;

typedef enum {
    BridgeTransceiveFrame = 0, /* standard frame (bits may be any length) */
    BridgeTransceiveReqa = 1, /* type A short frame 26 (data ignored) */
    BridgeTransceiveWupa = 2, /* type A short frame 52 (data ignored) */
    BridgeTransceiveSdd = 3, /* type A anticollision frame (bit oriented, split byte allowed) */
    BridgeTransceiveCustomParity = 4, /* type A frame with the host's parity bits (BridgeTxCustomParity implied) */
} BridgeTransceiveKind;

typedef enum {
    BridgeNfcErrorNone = 0,
    BridgeNfcErrorTimeout = 1,
    BridgeNfcErrorIncompleteFrame = 2,
    BridgeNfcErrorDataFormat = 3,
    BridgeNfcErrorInternal = 4,
    BridgeNfcErrorWrongMode = 5,
    BridgeNfcErrorBadArguments = 6,
} BridgeNfcError;
