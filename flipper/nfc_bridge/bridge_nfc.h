#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * The NFC side of the bridge, on the firmware's technology level Nfc API (no protocol listeners/pollers):
 *   listen: the Flipper is the card. Type A anticollision runs in the ST25R3916; every other frame is answered from
 *           the answer table or relayed to the host.
 *   reader: the Flipper is the reader. The host's TRANSCEIVE requests run on the NFC worker thread.
 */

typedef struct BridgeNfc BridgeNfc;

/* Sends one message to the host (thread safe). */
typedef bool (*BridgeSendCallback)(void* context, uint8_t type, uint8_t seq, const uint8_t* payload, size_t length);

typedef enum {
    BridgeModeOff,
    BridgeModeListen,
    BridgeModeReader,
} BridgeMode;

typedef struct {
    uint32_t frames;
    uint32_t answered;
    uint32_t timeouts;
    uint32_t late;
    uint32_t worst_us;
    uint32_t table_hits;
    uint32_t transceives;
} BridgeStats;

/* A snapshot for the UI. */
typedef struct {
    BridgeMode mode;
    uint8_t tech;
    bool field;
    uint8_t uid[10];
    uint8_t uid_length;
    size_t table_entries;
    BridgeStats stats;
    char last_frame[24];
} BridgeView;

BridgeNfc* bridge_nfc_alloc(BridgeSendCallback send, void* context);
void bridge_nfc_free(BridgeNfc* bridge);

uint32_t bridge_nfc_capabilities(void);

/* Main thread only (they start and stop the NFC worker). Return a BridgeStatus. */
uint8_t bridge_nfc_listen(BridgeNfc* bridge, const uint8_t* payload, size_t length);
uint8_t bridge_nfc_reader(BridgeNfc* bridge, const uint8_t* payload, size_t length);
uint8_t bridge_nfc_set_timing(BridgeNfc* bridge, const uint8_t* payload, size_t length);
void bridge_nfc_stop(BridgeNfc* bridge);

/* Any thread. */
void bridge_nfc_answer(BridgeNfc* bridge, uint8_t seq, const uint8_t* payload, size_t length);
void bridge_nfc_transceive(BridgeNfc* bridge, uint8_t seq, const uint8_t* payload, size_t length);
uint8_t bridge_nfc_field_reset(BridgeNfc* bridge);
uint8_t bridge_nfc_table_set(BridgeNfc* bridge, const uint8_t* payload, size_t length);
uint8_t bridge_nfc_table_remove(BridgeNfc* bridge, const uint8_t* payload, size_t length);
void bridge_nfc_table_clear(BridgeNfc* bridge);
void bridge_nfc_get_stats(BridgeNfc* bridge, BridgeStats* stats);
void bridge_nfc_get_view(BridgeNfc* bridge, BridgeView* view);

/* A µs clock (wraps after ~71 minutes). Call at least once a minute to keep it extended. */
uint32_t bridge_micros(void);
