#include "bridge_nfc.h"
#include "answer_table.h"
#include "bridge_crc.h"
#include "bridge_protocol.h"

#include <furi.h>
#include <furi_hal.h>
#include <furi_hal_nfc.h>
#include <nfc/nfc.h>
#include <toolbox/bit_buffer.h>

#define TAG "NfcBridge"

#define DEFAULT_ANSWER_TIMEOUT_MS 100
#define ANSWER_QUEUE_SIZE 4
#define REQUEST_QUEUE_SIZE 2
#define TABLE_CAPACITY (16 * 1024)
#define POLLER_IDLE_WAIT_MS 5

/* Comment out on firmware without FeliCa card emulation in the Nfc layer. */
#define BRIDGE_HAS_FELICA_LISTENER

typedef struct {
    uint8_t seq;
    uint8_t flags;
    uint16_t bits;
    uint8_t data[BRIDGE_MAX_FRAME];
    uint8_t parity[BRIDGE_MAX_FRAME / 8 + 1];
} BridgeAnswer;

typedef enum {
    RequestTransceive,
    RequestFieldReset,
} RequestType;

typedef struct {
    RequestType type;
    uint8_t seq;
    uint8_t kind;
    uint8_t flags;
    uint16_t bits;
    uint32_t timeout_us;
    uint8_t data[BRIDGE_MAX_FRAME];
    uint8_t parity[BRIDGE_MAX_FRAME / 8 + 1];
} BridgeRequest;

typedef struct {
    uint32_t fdt_poll_fc;
    uint32_t fdt_listen_fc;
    uint32_t guard_us;
    uint32_t poll_poll_us;
    uint32_t mask_receive_fc;
} BridgeTiming;

struct BridgeNfc {
    BridgeSendCallback send;
    void* send_context;

    Nfc* nfc;
    bool running;
    BridgeMode mode;
    uint8_t tech;
    uint8_t listen_flags;
    uint32_t answer_timeout_ms;
    BridgeTiming timing;

    FuriMutex* mutex; /* stats, view, table */
    AnswerTable* table;
    FuriMessageQueue* answer_queue;
    FuriMessageQueue* request_queue;

    /* Used only on the NFC worker thread. */
    BitBuffer* tx;
    BitBuffer* rx;
    BridgeAnswer* answer;
    BridgeRequest* request;
    uint8_t* message;
    uint8_t* raw; /* parity extraction scratch */
    uint8_t next_seq;
    bool transmitted; /* whether the current frame got an answer on air */

    bool field;
    uint8_t uid[10];
    uint8_t uid_length;
    BridgeStats stats;
    char last_frame[24];
};

/* ---- helpers ---- */

static void put_u16(uint8_t* p, uint16_t v) {
    p[0] = v & 0xFF;
    p[1] = v >> 8;
}

static void put_u32(uint8_t* p, uint32_t v) {
    p[0] = v & 0xFF;
    p[1] = (v >> 8) & 0xFF;
    p[2] = (v >> 16) & 0xFF;
    p[3] = v >> 24;
}

static uint16_t get_u16(const uint8_t* p) {
    return p[0] | (p[1] << 8);
}

static uint32_t get_u32(const uint8_t* p) {
    return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24);
}

static size_t bits_to_bytes(size_t bits) {
    return (bits + 7) / 8;
}

static size_t parity_bytes(size_t bytes) {
    return (bytes + 7) / 8;
}

uint32_t bridge_micros(void) {
    static uint32_t last_cycles = 0;
    static uint64_t total_cycles = 0;
    static bool started = false;

    FURI_CRITICAL_ENTER();
    if(!started) {
        CoreDebug->DEMCR |= CoreDebug_DEMCR_TRCENA_Msk;
        DWT->CTRL |= DWT_CTRL_CYCCNTENA_Msk;
        last_cycles = DWT->CYCCNT;
        started = true;
    }
    const uint32_t cycles = DWT->CYCCNT;
    total_cycles += (uint32_t)(cycles - last_cycles);
    last_cycles = cycles;
    const uint64_t total = total_cycles;
    FURI_CRITICAL_EXIT();

    return (uint32_t)(total / furi_hal_cortex_instructions_per_microsecond());
}

static void bridge_send_event(BridgeNfc* bridge, uint8_t type) {
    uint8_t payload[4];
    put_u32(payload, bridge_micros());
    bridge->send(bridge->send_context, type, 0, payload, sizeof(payload));
}

static void bridge_describe(BridgeNfc* bridge, const uint8_t* data, size_t bytes) {
    size_t pos = 0;
    for(size_t i = 0; i < bytes && pos + 3 < sizeof(bridge->last_frame); i++) {
        pos += snprintf(&bridge->last_frame[pos], sizeof(bridge->last_frame) - pos, "%02X ", data[i]);
    }
    if(pos > 0 && bytes * 3 >= sizeof(bridge->last_frame)) {
        bridge->last_frame[pos - 1] = '~';
    }
    if(pos == 0) {
        bridge->last_frame[0] = '\0';
    }
}

/* Fills a bit buffer from bits/data[/parity], optionally appending the CRC. */
static bool bridge_fill(
    BridgeNfc* bridge,
    BitBuffer* buffer,
    uint8_t flags,
    uint16_t bits,
    const uint8_t* data,
    const uint8_t* parity) {
    const size_t bytes = bits_to_bytes(bits);
    const bool add_crc = (flags & BridgeTxAddCrc) != 0;
    if(add_crc && (bits % 8) != 0) {
        return false;
    }
    if(bytes + (add_crc ? 2 : 0) > BRIDGE_MAX_FRAME) {
        return false;
    }

    uint8_t crc[2] = {0};
    if(add_crc) {
        bridge_crc_compute(bridge->tech, data, bytes, crc);
    }

    if(flags & BridgeTxCustomParity) {
        const size_t total = bytes + (add_crc ? 2 : 0);
        bit_buffer_reset(buffer);
        bit_buffer_set_size_bytes(buffer, total);
        for(size_t i = 0; i < total; i++) {
            const uint8_t byte = i < bytes ? data[i] : crc[i - bytes];
            /* The CRC bytes, which the host didn't give parity for, get standard odd parity. */
            const bool p = (parity != NULL && i < bytes) ? ((parity[i / 8] >> (i % 8)) & 1) :
                                                           !(__builtin_popcount(byte) & 1);
            bit_buffer_set_byte_with_parity(buffer, i, byte, p);
        }
        return true;
    }

    bit_buffer_copy_bytes(buffer, data, bytes);
    if(add_crc) {
        bit_buffer_append_bytes(buffer, crc, 2);
    } else if(bits % 8) {
        bit_buffer_set_size(buffer, bits);
    }
    return true;
}

/* Writes the received frame's data and (if asked) parity bitmap into msg; returns the bytes written. */
static size_t bridge_put_frame(
    BridgeNfc* bridge,
    const BitBuffer* frame,
    bool check_crc,
    bool want_parity,
    uint8_t* flags,
    uint16_t* bits_out,
    uint8_t* out,
    size_t room) {
    size_t bits = bit_buffer_get_size(frame);
    size_t bytes = bit_buffer_get_size_bytes(frame);
    const uint8_t* data = bit_buffer_get_data(frame);

    if(check_crc && (bits % 8) == 0 && bridge_crc_check(bridge->tech, data, bytes)) {
        bytes -= 2;
        bits -= 16;
        *flags |= BridgeFrameCrcOk;
    }

    bytes = MIN(bytes, MIN(room, (size_t)BRIDGE_MAX_FRAME));
    memcpy(out, data, bytes);
    *bits_out = MIN(bits, bytes * 8);
    size_t written = bytes;

    if(want_parity && bridge->tech == BridgeTechIso14443a && room - bytes >= parity_bytes(bytes)) {
        /* The firmware hands out parity interleaved after each byte (9 bits per byte, LSB first). */
        size_t raw_bits = 0;
        bit_buffer_write_bytes_with_parity(frame, bridge->raw, BRIDGE_MAX_FRAME * 9 / 8 + 2, &raw_bits);
        uint8_t* parity = out + bytes;
        memset(parity, 0, parity_bytes(bytes));
        for(size_t i = 0; i < bytes; i++) {
            const size_t pos = i * 9 + 8;
            if(pos < raw_bits && ((bridge->raw[pos / 8] >> (pos % 8)) & 1)) {
                parity[i / 8] |= 1 << (i % 8);
            }
        }
        written += parity_bytes(bytes);
        *flags |= BridgeFrameParity;
    }

    return written;
}

/* ---- listen ---- */

static NfcCommand bridge_listener_transmit(BridgeNfc* bridge, uint8_t flags, uint16_t bits, const uint8_t* data, const uint8_t* parity) {
    if(bits == 0) {
        return (flags & BridgeTxSleep) ? NfcCommandSleep : NfcCommandContinue;
    }

    if(!bridge_fill(bridge, bridge->tx, flags, bits, data, parity)) {
        return NfcCommandContinue;
    }

    NfcError error;
    if((flags & BridgeTxCustomParity) && bridge->tech == BridgeTechIso14443a) {
        error = nfc_iso14443a_listener_tx_custom_parity(bridge->nfc, bridge->tx);
    } else {
        error = nfc_listener_tx(bridge->nfc, bridge->tx);
    }
    /* Counts even when the call reports an error: a long frame outlasts the driver's wait while the chip keeps
       sending, and restarting reception then would cut it off mid-frame. */
    bridge->transmitted = true;
    if(error != NfcErrorNone) {
        FURI_LOG_W(TAG, "Listener tx of %u bits reported error %d", bits, error);
    }

    return (flags & BridgeTxSleep) ? NfcCommandSleep : NfcCommandContinue;
}

/* Waits for the host's ANSWER to FRAME seq. */
static bool bridge_wait_answer(BridgeNfc* bridge, uint8_t seq, uint32_t started) {
    const uint32_t deadline = furi_get_tick() + furi_ms_to_ticks(bridge->answer_timeout_ms);
    while(true) {
        const int32_t remaining = (int32_t)(deadline - furi_get_tick());
        if(remaining <= 0 || furi_message_queue_get(bridge->answer_queue, bridge->answer, remaining) != FuriStatusOk) {
            furi_mutex_acquire(bridge->mutex, FuriWaitForever);
            bridge->stats.timeouts++;
            furi_mutex_release(bridge->mutex);
            return false;
        }

        if(bridge->answer->seq == seq) {
            break;
        }

        furi_mutex_acquire(bridge->mutex, FuriWaitForever);
        bridge->stats.late++;
        furi_mutex_release(bridge->mutex);
    }

    const uint32_t elapsed = bridge_micros() - started;
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    bridge->stats.answered++;
    if(elapsed > bridge->stats.worst_us) {
        bridge->stats.worst_us = elapsed;
    }
    furi_mutex_release(bridge->mutex);
    return true;
}

static NfcCommand bridge_listener_handle_rx(BridgeNfc* bridge, const BitBuffer* frame) {
    const uint32_t started = bridge_micros();
    uint8_t* msg = bridge->message;
    uint8_t flags = 0;
    uint16_t bits = 0;
    const size_t written = bridge_put_frame(
        bridge,
        frame,
        bridge->listen_flags & BridgeListenStripCrc,
        bridge->listen_flags & BridgeListenReportParity,
        &flags,
        &bits,
        &msg[7],
        BRIDGE_MAX_PAYLOAD - 7);
    const uint8_t* data = &msg[7];
    const size_t bytes = bits_to_bytes(bits);

    /* HLTA, handled here so the host needn't answer it in time. */
    if(bridge->tech == BridgeTechIso14443a && (bridge->listen_flags & BridgeListenHandleHlta) &&
       (flags & BridgeFrameCrcOk) && bytes == 2 && data[0] == 0x50 && data[1] == 0x00) {
        bridge_send_event(bridge, BridgeMsgHalted);
        return NfcCommandSleep;
    }

    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    bridge->stats.frames++;
    bridge_describe(bridge, data, bytes);

    AnswerTableHit hit;
    if(answer_table_find(bridge->table, data, bytes, &hit)) {
        const NfcCommand command = bridge_listener_transmit(bridge, hit.flags, hit.bits, hit.data, hit.parity);
        bridge->stats.table_hits++;
        furi_mutex_release(bridge->mutex);

        if(bridge->listen_flags & BridgeListenNotifyTableHits) {
            put_u32(&msg[0], started);
            msg[4] = flags | BridgeFrameFromTable;
            put_u16(&msg[5], bits);
            bridge->send(bridge->send_context, BridgeMsgFrame, 0, msg, 7 + written);
        }
        return command;
    }
    furi_mutex_release(bridge->mutex);

    const uint8_t seq = bridge->next_seq++;
    put_u32(&msg[0], started);
    msg[4] = flags;
    put_u16(&msg[5], bits);
    if(!bridge->send(bridge->send_context, BridgeMsgFrame, seq, msg, 7 + written)) {
        return NfcCommandContinue;
    }

    if(!bridge_wait_answer(bridge, seq, started)) {
        return NfcCommandContinue;
    }

    BridgeAnswer* answer = bridge->answer;
    return bridge_listener_transmit(
        bridge, answer->flags, answer->bits, answer->data, (answer->flags & BridgeTxCustomParity) ? answer->parity : NULL);
}

/*
 * The ST25R3916 stops receiving after each frame and only restarts with the next transmission. A frame left
 * unanswered on purpose (a passive ACK such as SECTOR_SELECT's), or because the host was too late, would leave the
 * card deaf to everything that follows, so reception is restarted by hand.
 */
static NfcCommand bridge_listener_rx(BridgeNfc* bridge, const BitBuffer* frame) {
    bridge->transmitted = false;
    const NfcCommand command = bridge_listener_handle_rx(bridge, frame);
    if(command == NfcCommandContinue && !bridge->transmitted) {
        furi_hal_nfc_listener_enable_rx();
    }
    return command;
}

static NfcCommand bridge_listener_callback(NfcEvent event, void* context) {
    BridgeNfc* bridge = context;

    switch(event.type) {
    case NfcEventTypeFieldOn:
        bridge->field = true;
        bridge_send_event(bridge, BridgeMsgFieldOn);
        return NfcCommandContinue;

    case NfcEventTypeFieldOff:
        bridge->field = false;
        bridge_send_event(bridge, BridgeMsgFieldOff);
        return NfcCommandContinue;

    case NfcEventTypeListenerActivated:
        bridge->field = true;
        bridge_send_event(bridge, BridgeMsgActivated);
        return NfcCommandContinue;

    case NfcEventTypeRxEnd:
        bridge->field = true;
        return bridge_listener_rx(bridge, event.data.buffer);

    default:
        return NfcCommandContinue;
    }
}

/* ---- reader ---- */

static uint8_t bridge_map_error(NfcError error) {
    switch(error) {
    case NfcErrorNone:
        return BridgeNfcErrorNone;
    case NfcErrorTimeout:
        return BridgeNfcErrorTimeout;
    case NfcErrorIncompleteFrame:
        return BridgeNfcErrorIncompleteFrame;
    case NfcErrorDataFormat:
        return BridgeNfcErrorDataFormat;
    default:
        return BridgeNfcErrorInternal;
    }
}

static void bridge_send_result(BridgeNfc* bridge, uint8_t seq, uint8_t error, uint32_t elapsed, const BitBuffer* rx, uint8_t check_flags, bool parity) {
    uint8_t* msg = bridge->message;
    uint8_t flags = 0;
    uint16_t bits = 0;
    size_t written = 0;
    if(rx != NULL && error == BridgeNfcErrorNone) {
        written = bridge_put_frame(bridge, rx, check_flags & BridgeTxCheckCrc, parity, &flags, &bits, &msg[9], BRIDGE_MAX_PAYLOAD - 9);
    }
    msg[0] = error;
    msg[1] = flags;
    put_u16(&msg[2], bits);
    put_u32(&msg[4], elapsed);
    /* msg[8] reserved so the data starts at a fixed offset. */
    msg[8] = 0;
    bridge->send(bridge->send_context, BridgeMsgTransceiveResult, seq, msg, 9 + written);
}

static void bridge_run_transceive(BridgeNfc* bridge, const BridgeRequest* request) {
    const uint32_t fwt_fc = (uint32_t)(((uint64_t)request->timeout_us * 1356) / 100);
    const bool type_a = bridge->tech == BridgeTechIso14443a;
    const uint8_t kind = request->kind;
    uint8_t tx_flags = request->flags & (BridgeTxAddCrc | BridgeTxCustomParity);
    if(kind == BridgeTransceiveCustomParity) {
        tx_flags |= BridgeTxCustomParity;
    }

    if(!type_a && kind != BridgeTransceiveFrame) {
        bridge_send_result(bridge, request->seq, BridgeNfcErrorBadArguments, 0, NULL, 0, false);
        return;
    }

    bit_buffer_reset(bridge->rx);
    const uint32_t started = bridge_micros();
    NfcError error;

    switch(kind) {
    case BridgeTransceiveReqa:
    case BridgeTransceiveWupa:
        error = nfc_iso14443a_poller_trx_short_frame(
            bridge->nfc,
            kind == BridgeTransceiveReqa ? NfcIso14443aShortFrameSensReq : NfcIso14443aShortFrameAllReqa,
            bridge->rx,
            fwt_fc);
        break;

    case BridgeTransceiveSdd:
        if(!bridge_fill(bridge, bridge->tx, 0, request->bits, request->data, NULL)) {
            bridge_send_result(bridge, request->seq, BridgeNfcErrorBadArguments, 0, NULL, 0, false);
            return;
        }
        error = nfc_iso14443a_poller_trx_sdd_frame(bridge->nfc, bridge->tx, bridge->rx, fwt_fc);
        break;

    default:
        if(request->bits == 0 ||
           !bridge_fill(
               bridge,
               bridge->tx,
               tx_flags,
               request->bits,
               request->data,
               (tx_flags & BridgeTxCustomParity) ? request->parity : NULL)) {
            bridge_send_result(bridge, request->seq, BridgeNfcErrorBadArguments, 0, NULL, 0, false);
            return;
        }
        if(type_a && (tx_flags & BridgeTxCustomParity)) {
            error = nfc_iso14443a_poller_trx_custom_parity(bridge->nfc, bridge->tx, bridge->rx, fwt_fc);
        } else {
            error = nfc_poller_trx(bridge->nfc, bridge->tx, bridge->rx, fwt_fc);
        }
        break;
    }

    const uint32_t elapsed = bridge_micros() - started;
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    bridge->stats.transceives++;
    if(error == NfcErrorTimeout) bridge->stats.timeouts++;
    bridge_describe(bridge, request->data, bits_to_bytes(request->bits));
    furi_mutex_release(bridge->mutex);

    bridge_send_result(
        bridge, request->seq, bridge_map_error(error), elapsed, bridge->rx, request->flags, (tx_flags & BridgeTxCustomParity) != 0);
}

static NfcCommand bridge_poller_callback(NfcEvent event, void* context) {
    BridgeNfc* bridge = context;
    if(event.type != NfcEventTypePollerReady) {
        return NfcCommandContinue;
    }

    bridge->field = true;
    if(furi_message_queue_get(bridge->request_queue, bridge->request, POLLER_IDLE_WAIT_MS) != FuriStatusOk) {
        return NfcCommandContinue;
    }

    if(bridge->request->type == RequestFieldReset) {
        return NfcCommandReset;
    }

    bridge_run_transceive(bridge, bridge->request);
    return NfcCommandContinue;
}

/* ---- control ---- */

uint32_t bridge_nfc_capabilities(void) {
    uint32_t caps = BridgeCapListenA | BridgeCapListenV | BridgeCapReaderA | BridgeCapReaderB | BridgeCapReaderV |
                    BridgeCapReaderF | BridgeCapAnswerTable | BridgeCapCustomParity;
#ifdef BRIDGE_HAS_FELICA_LISTENER
    caps |= BridgeCapListenF;
#endif
    return caps;
}

static NfcTech bridge_nfc_tech(uint8_t tech) {
    switch(tech) {
    case BridgeTechIso14443b:
        return NfcTechIso14443b;
    case BridgeTechIso15693:
        return NfcTechIso15693;
    case BridgeTechFelica:
        return NfcTechFelica;
    default:
        return NfcTechIso14443a;
    }
}

BridgeNfc* bridge_nfc_alloc(BridgeSendCallback send, void* context) {
    BridgeNfc* bridge = malloc(sizeof(BridgeNfc));
    memset(bridge, 0, sizeof(BridgeNfc));
    bridge->send = send;
    bridge->send_context = context;
    bridge->nfc = nfc_alloc();
    bridge->mutex = furi_mutex_alloc(FuriMutexTypeNormal);
    bridge->table = answer_table_alloc(TABLE_CAPACITY);
    bridge->answer_queue = furi_message_queue_alloc(ANSWER_QUEUE_SIZE, sizeof(BridgeAnswer));
    bridge->request_queue = furi_message_queue_alloc(REQUEST_QUEUE_SIZE, sizeof(BridgeRequest));
    bridge->tx = bit_buffer_alloc(BRIDGE_MAX_FRAME + 2);
    bridge->rx = bit_buffer_alloc(BRIDGE_MAX_FRAME + 2);
    bridge->answer = malloc(sizeof(BridgeAnswer));
    bridge->request = malloc(sizeof(BridgeRequest));
    bridge->message = malloc(BRIDGE_MAX_PAYLOAD);
    bridge->raw = malloc(BRIDGE_MAX_FRAME * 9 / 8 + 2);
    bridge->answer_timeout_ms = DEFAULT_ANSWER_TIMEOUT_MS;
    bridge_micros();
    return bridge;
}

void bridge_nfc_free(BridgeNfc* bridge) {
    bridge_nfc_stop(bridge);
    nfc_free(bridge->nfc);
    furi_mutex_free(bridge->mutex);
    answer_table_free(bridge->table);
    furi_message_queue_free(bridge->answer_queue);
    furi_message_queue_free(bridge->request_queue);
    bit_buffer_free(bridge->tx);
    bit_buffer_free(bridge->rx);
    free(bridge->answer);
    free(bridge->request);
    free(bridge->message);
    free(bridge->raw);
    free(bridge);
}

void bridge_nfc_stop(BridgeNfc* bridge) {
    if(bridge->running) {
        nfc_stop(bridge->nfc);
        bridge->running = false;
    }
    bridge->mode = BridgeModeOff;
    bridge->field = false;
    furi_message_queue_reset(bridge->answer_queue);
    furi_message_queue_reset(bridge->request_queue);
}

static void bridge_reset_stats(BridgeNfc* bridge) {
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    memset(&bridge->stats, 0, sizeof(bridge->stats));
    bridge->last_frame[0] = '\0';
    furi_mutex_release(bridge->mutex);
}

uint8_t bridge_nfc_set_timing(BridgeNfc* bridge, const uint8_t* p, size_t length) {
    if(length < 20) {
        return BridgeStatusBadArguments;
    }
    bridge->timing.fdt_poll_fc = get_u32(&p[0]);
    bridge->timing.fdt_listen_fc = get_u32(&p[4]);
    bridge->timing.guard_us = get_u32(&p[8]);
    bridge->timing.poll_poll_us = get_u32(&p[12]);
    bridge->timing.mask_receive_fc = get_u32(&p[16]);
    return BridgeStatusOk;
}

uint8_t bridge_nfc_listen(BridgeNfc* bridge, const uint8_t* p, size_t length) {
    if(length < 4) {
        return BridgeStatusBadArguments;
    }

    const uint8_t tech = p[0];
    const uint8_t flags = p[1];
    const uint16_t timeout = get_u16(&p[2]);
    const uint8_t* params = &p[4];
    const size_t params_length = length - 4;
    uint8_t uid[10] = {0};
    uint8_t uid_length = 0;

    switch(tech) {
    case BridgeTechIso14443a:
        /* u8 uid length, uid, atqa[2] (air order), sak */
        if(params_length < 1) return BridgeStatusBadArguments;
        uid_length = params[0];
        if((uid_length != 4 && uid_length != 7 && uid_length != 10) || params_length < 1u + uid_length + 3u) {
            return BridgeStatusBadArguments;
        }
        memcpy(uid, &params[1], uid_length);
        break;
    case BridgeTechIso15693:
        break;
    case BridgeTechFelica:
#ifdef BRIDGE_HAS_FELICA_LISTENER
        /* idm[8], pmm[8], u16 system code */
        if(params_length < 18) return BridgeStatusBadArguments;
        memcpy(uid, params, 8);
        uid_length = 8;
        break;
#else
        return BridgeStatusUnsupported;
#endif
    default:
        return BridgeStatusUnsupported;
    }

    bridge_nfc_stop(bridge);
    bridge_reset_stats(bridge);
    bridge->tech = tech;
    bridge->listen_flags = flags;
    bridge->answer_timeout_ms = timeout ? timeout : DEFAULT_ANSWER_TIMEOUT_MS;
    memcpy(bridge->uid, uid, sizeof(uid));
    bridge->uid_length = uid_length;

    nfc_config(bridge->nfc, NfcModeListener, bridge_nfc_tech(tech));
    if(bridge->timing.fdt_listen_fc) nfc_set_fdt_listen_fc(bridge->nfc, bridge->timing.fdt_listen_fc);
    if(bridge->timing.mask_receive_fc) nfc_set_mask_receive_time_fc(bridge->nfc, bridge->timing.mask_receive_fc);

    if(tech == BridgeTechIso14443a) {
        uint8_t atqa[2] = {params[1 + uid_length], params[2 + uid_length]};
        nfc_iso14443a_listener_set_col_res_data(bridge->nfc, uid, uid_length, atqa, params[3 + uid_length]);
    }
#ifdef BRIDGE_HAS_FELICA_LISTENER
    else if(tech == BridgeTechFelica) {
        nfc_felica_listener_set_sensf_res_data(bridge->nfc, &params[0], 8, &params[8], 8, get_u16(&params[16]));
    }
#endif

    nfc_start(bridge->nfc, bridge_listener_callback, bridge);
    bridge->running = true;
    bridge->mode = BridgeModeListen;
    FURI_LOG_I(TAG, "Listening, tech %u, flags %02X, timeout %lu ms", tech, flags, (unsigned long)bridge->answer_timeout_ms);
    return BridgeStatusOk;
}

uint8_t bridge_nfc_reader(BridgeNfc* bridge, const uint8_t* p, size_t length) {
    if(length < 1 || p[0] > BridgeTechFelica) {
        return BridgeStatusBadArguments;
    }

    /* Defaults like the firmware's own pollers use; SET_TIMING overrides them. */
    static const BridgeTiming defaults[] = {
        [BridgeTechIso14443a] = {.fdt_poll_fc = 1620, .guard_us = 5000, .poll_poll_us = 1100},
        [BridgeTechIso14443b] = {.fdt_poll_fc = 9000, .guard_us = 5000, .poll_poll_us = 1100},
        [BridgeTechIso15693] = {.fdt_poll_fc = 4202, .guard_us = 5000, .poll_poll_us = 1500},
        [BridgeTechFelica] = {.fdt_poll_fc = 10000, .guard_us = 20000, .poll_poll_us = 1100},
    };

    const uint8_t tech = p[0];
    bridge_nfc_stop(bridge);
    bridge_reset_stats(bridge);
    bridge->tech = tech;
    bridge->uid_length = 0;

    const BridgeTiming* d = &defaults[tech];
    const BridgeTiming* t = &bridge->timing;
    nfc_config(bridge->nfc, NfcModePoller, bridge_nfc_tech(tech));
    nfc_set_guard_time_us(bridge->nfc, t->guard_us ? t->guard_us : d->guard_us);
    nfc_set_fdt_poll_fc(bridge->nfc, t->fdt_poll_fc ? t->fdt_poll_fc : d->fdt_poll_fc);
    nfc_set_fdt_poll_poll_us(bridge->nfc, t->poll_poll_us ? t->poll_poll_us : d->poll_poll_us);
    if(t->mask_receive_fc) nfc_set_mask_receive_time_fc(bridge->nfc, t->mask_receive_fc);

    nfc_start(bridge->nfc, bridge_poller_callback, bridge);
    bridge->running = true;
    bridge->mode = BridgeModeReader;
    FURI_LOG_I(TAG, "Reader, tech %u", tech);
    return BridgeStatusOk;
}

void bridge_nfc_answer(BridgeNfc* bridge, uint8_t seq, const uint8_t* p, size_t length) {
    if(bridge->mode != BridgeModeListen || length < 3) {
        return;
    }

    BridgeAnswer* answer = malloc(sizeof(BridgeAnswer));
    answer->seq = seq;
    answer->flags = p[0];
    answer->bits = get_u16(&p[1]);
    const size_t bytes = bits_to_bytes(answer->bits);
    const size_t parity = (answer->flags & BridgeTxCustomParity) ? parity_bytes(bytes) : 0;
    if(bytes <= BRIDGE_MAX_FRAME && length >= 3 + bytes + parity) {
        memcpy(answer->data, &p[3], bytes);
        memcpy(answer->parity, &p[3 + bytes], parity);
        /* Never wait: if the queue is full nobody is waiting for this answer anyway. */
        furi_message_queue_put(bridge->answer_queue, answer, 0);
    }
    free(answer);
}

void bridge_nfc_transceive(BridgeNfc* bridge, uint8_t seq, const uint8_t* p, size_t length) {
    uint8_t error[9] = {0};

    if(bridge->mode != BridgeModeReader) {
        error[0] = BridgeNfcErrorWrongMode;
        bridge->send(bridge->send_context, BridgeMsgTransceiveResult, seq, error, sizeof(error));
        return;
    }

    BridgeRequest* request = malloc(sizeof(BridgeRequest));
    bool ok = length >= 8;
    if(ok) {
        request->type = RequestTransceive;
        request->seq = seq;
        request->kind = p[0];
        request->flags = p[1];
        request->bits = get_u16(&p[2]);
        request->timeout_us = get_u32(&p[4]);
        const size_t bytes = bits_to_bytes(request->bits);
        const bool custom = request->kind == BridgeTransceiveCustomParity || (request->flags & BridgeTxCustomParity);
        const size_t parity = custom ? parity_bytes(bytes) : 0;
        ok = bytes <= BRIDGE_MAX_FRAME && length >= 8 + bytes + parity;
        if(ok) {
            memcpy(request->data, &p[8], bytes);
            memcpy(request->parity, &p[8 + bytes], parity);
            if(request->timeout_us == 0) request->timeout_us = 5000;
            ok = furi_message_queue_put(bridge->request_queue, request, 100) == FuriStatusOk;
        }
    }
    free(request);

    if(!ok) {
        error[0] = BridgeNfcErrorBadArguments;
        bridge->send(bridge->send_context, BridgeMsgTransceiveResult, seq, error, sizeof(error));
    }
}

uint8_t bridge_nfc_field_reset(BridgeNfc* bridge) {
    if(bridge->mode != BridgeModeReader) {
        return BridgeStatusWrongMode;
    }
    BridgeRequest* request = malloc(sizeof(BridgeRequest));
    request->type = RequestFieldReset;
    const bool ok = furi_message_queue_put(bridge->request_queue, request, 100) == FuriStatusOk;
    free(request);
    return ok ? BridgeStatusOk : BridgeStatusWrongMode;
}

uint8_t bridge_nfc_table_set(BridgeNfc* bridge, const uint8_t* p, size_t length) {
    if(length < 4) {
        return BridgeStatusBadArguments;
    }
    const uint8_t flags = p[0];
    const uint8_t match_length = p[1];
    const uint16_t bits = get_u16(&p[2]);
    const size_t bytes = bits_to_bytes(bits);
    const size_t parity = (flags & BridgeTxCustomParity) ? parity_bytes(bytes) : 0;
    if(match_length == 0 || length < 4u + match_length + bytes + parity) {
        return BridgeStatusBadArguments;
    }

    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    const bool ok = answer_table_set(
        bridge->table, flags, &p[4], match_length, bits, &p[4 + match_length], parity ? &p[4 + match_length + bytes] : NULL);
    furi_mutex_release(bridge->mutex);
    return ok ? BridgeStatusOk : BridgeStatusTableFull;
}

uint8_t bridge_nfc_table_remove(BridgeNfc* bridge, const uint8_t* p, size_t length) {
    if(length < 1 || length < 1u + p[0]) {
        return BridgeStatusBadArguments;
    }
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    const bool ok = answer_table_remove(bridge->table, &p[1], p[0]);
    furi_mutex_release(bridge->mutex);
    return ok ? BridgeStatusOk : BridgeStatusNotFound;
}

void bridge_nfc_table_clear(BridgeNfc* bridge) {
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    answer_table_clear(bridge->table);
    furi_mutex_release(bridge->mutex);
}

void bridge_nfc_get_stats(BridgeNfc* bridge, BridgeStats* stats) {
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    *stats = bridge->stats;
    furi_mutex_release(bridge->mutex);
}

void bridge_nfc_get_view(BridgeNfc* bridge, BridgeView* view) {
    furi_mutex_acquire(bridge->mutex, FuriWaitForever);
    view->mode = bridge->mode;
    view->tech = bridge->tech;
    view->field = bridge->field;
    memcpy(view->uid, bridge->uid, sizeof(view->uid));
    view->uid_length = bridge->uid_length;
    view->table_entries = answer_table_count(bridge->table);
    view->stats = bridge->stats;
    memcpy(view->last_frame, bridge->last_frame, sizeof(view->last_frame));
    furi_mutex_release(bridge->mutex);
}
