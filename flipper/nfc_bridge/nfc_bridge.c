/*
 * NFC Bridge: exposes the Flipper's NFC front end to a computer over USB HID, as a card (listen) or as a
 * reader, for ISO 14443-A/B, ISO 15693 and FeliCa. See README.md and docs/PROTOCOL.md.
 */

#include "bridge_nfc.h"
#include "bridge_protocol.h"
#include "usb_bridge.h"

#include <furi.h>
#include <furi_hal.h>
#include <gui/gui.h>
#include <input/input.h>

#define TAG "NfcBridge"
#define USB_SEND_TIMEOUT_MS 20
#define FIRMWARE_NAME "NFC Bridge 2.0"

/* Messages that must run on the main thread (they start and stop the NFC worker). */
typedef struct {
    uint8_t type;
    uint8_t length;
    uint8_t data[48];
} BridgeControl;

typedef struct {
    Gui* gui;
    ViewPort* view_port;
    FuriMessageQueue* input_queue;
    FuriMessageQueue* control_queue;
    FuriMutex* tx_mutex;
    FuriThread* rx_thread;
    volatile bool running;

    UsbBridge* usb;
    BridgeNfc* nfc;
} BridgeApp;

typedef struct {
    uint8_t type;
    uint8_t seq;
    uint8_t expected_chunk;
    uint8_t chunks;
    uint16_t length;
    uint16_t received;
    uint8_t data[BRIDGE_MAX_PAYLOAD];
} BridgeAssembly;

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

/* ---- to the host ---- */

static bool bridge_send(void* context, uint8_t type, uint8_t seq, const uint8_t* payload, size_t length) {
    BridgeApp* app = context;
    if(length > BRIDGE_MAX_PAYLOAD) {
        return false;
    }

    const uint8_t chunks = length == 0 ? 1 : (length + BRIDGE_CHUNK_PAYLOAD - 1) / BRIDGE_CHUNK_PAYLOAD;
    bool ok = true;

    furi_mutex_acquire(app->tx_mutex, FuriWaitForever);
    for(uint8_t i = 0; i < chunks && ok; i++) {
        uint8_t report[BRIDGE_REPORT_SIZE] = {0};
        report[0] = type;
        report[1] = seq;
        report[2] = i;
        report[3] = chunks;
        put_u16(&report[4], length);
        const size_t offset = (size_t)i * BRIDGE_CHUNK_PAYLOAD;
        const size_t count = MIN((size_t)BRIDGE_CHUNK_PAYLOAD, length - offset);
        if(count > 0) {
            memcpy(&report[BRIDGE_HEADER_SIZE], payload + offset, count);
        }
        ok = usb_bridge_send(app->usb, report, USB_SEND_TIMEOUT_MS);
    }
    furi_mutex_release(app->tx_mutex);
    return ok;
}

static void bridge_ack(BridgeApp* app, uint8_t type, uint8_t status) {
    const uint8_t payload[2] = {type, status};
    bridge_send(app, BridgeMsgAck, 0, payload, sizeof(payload));
}

static void bridge_send_hello(BridgeApp* app) {
    static const char name[] = FIRMWARE_NAME;
    uint8_t payload[8 + sizeof(name)];
    payload[0] = BRIDGE_PROTOCOL_VERSION;
    payload[1] = 0;
    put_u16(&payload[2], BRIDGE_MAX_PAYLOAD);
    put_u32(&payload[4], bridge_nfc_capabilities());
    memcpy(&payload[8], name, sizeof(name) - 1);
    bridge_send(app, BridgeMsgHello, 0, payload, 8 + sizeof(name) - 1);
}

static void bridge_send_status(BridgeApp* app) {
    BridgeStats stats;
    bridge_nfc_get_stats(app->nfc, &stats);
    uint8_t payload[28];
    put_u32(&payload[0], stats.frames);
    put_u32(&payload[4], stats.answered);
    put_u32(&payload[8], stats.timeouts);
    put_u32(&payload[12], stats.late);
    put_u32(&payload[16], stats.worst_us);
    put_u32(&payload[20], stats.table_hits);
    put_u32(&payload[24], stats.transceives);
    bridge_send(app, BridgeMsgStatus, 0, payload, sizeof(payload));
}

/* ---- from the host ---- */

static void bridge_handle_message(BridgeApp* app, const BridgeAssembly* msg) {
    switch(msg->type) {
    case BridgeMsgAnswer:
        bridge_nfc_answer(app->nfc, msg->seq, msg->data, msg->length);
        break;

    case BridgeMsgTransceive:
        bridge_nfc_transceive(app->nfc, msg->seq, msg->data, msg->length);
        break;

    case BridgeMsgHostHello:
        bridge_send_hello(app);
        break;

    case BridgeMsgGetStatus:
        bridge_send_status(app);
        break;

    case BridgeMsgFieldReset:
        bridge_ack(app, msg->type, bridge_nfc_field_reset(app->nfc));
        break;

    case BridgeMsgTableClear:
        bridge_nfc_table_clear(app->nfc);
        bridge_ack(app, msg->type, BridgeStatusOk);
        break;

    case BridgeMsgTableSet:
        bridge_ack(app, msg->type, bridge_nfc_table_set(app->nfc, msg->data, msg->length));
        break;

    case BridgeMsgTableRemove:
        bridge_ack(app, msg->type, bridge_nfc_table_remove(app->nfc, msg->data, msg->length));
        break;

    case BridgeMsgListen:
    case BridgeMsgReader:
    case BridgeMsgStop:
    case BridgeMsgSetTiming: {
        if(msg->length > sizeof(((BridgeControl*)0)->data)) {
            bridge_ack(app, msg->type, BridgeStatusBadArguments);
            break;
        }
        BridgeControl control = {.type = msg->type, .length = msg->length};
        memcpy(control.data, msg->data, msg->length);
        furi_message_queue_put(app->control_queue, &control, FuriWaitForever);
        break;
    }

    default:
        break;
    }
}

static int32_t bridge_rx_thread(void* context) {
    BridgeApp* app = context;
    BridgeAssembly* msg = malloc(sizeof(BridgeAssembly));
    msg->expected_chunk = 0xFF;
    uint8_t report[BRIDGE_REPORT_SIZE];

    while(app->running) {
        if(!usb_bridge_receive(app->usb, report, 50)) {
            continue;
        }

        const uint8_t chunk = report[2];
        const uint8_t chunks = report[3] ? report[3] : 1;
        const uint16_t length = report[4] | (report[5] << 8);
        if(length > BRIDGE_MAX_PAYLOAD) {
            continue;
        }

        if(chunk == 0) {
            msg->type = report[0];
            msg->seq = report[1];
            msg->chunks = chunks;
            msg->length = length;
            msg->received = 0;
        } else if(chunk != msg->expected_chunk || report[0] != msg->type || report[1] != msg->seq) {
            msg->expected_chunk = 0xFF; /* lost a chunk: drop the message */
            continue;
        }

        const uint16_t count = MIN((uint16_t)BRIDGE_CHUNK_PAYLOAD, (uint16_t)(msg->length - msg->received));
        memcpy(&msg->data[msg->received], &report[BRIDGE_HEADER_SIZE], count);
        msg->received += count;
        msg->expected_chunk = chunk + 1;

        if(msg->expected_chunk == msg->chunks) {
            bridge_handle_message(app, msg);
            msg->expected_chunk = 0xFF;
        }
    }

    free(msg);
    return 0;
}

static void bridge_run_control(BridgeApp* app, const BridgeControl* control) {
    uint8_t status = BridgeStatusOk;
    switch(control->type) {
    case BridgeMsgListen:
        status = bridge_nfc_listen(app->nfc, control->data, control->length);
        break;
    case BridgeMsgReader:
        status = bridge_nfc_reader(app->nfc, control->data, control->length);
        break;
    case BridgeMsgSetTiming:
        status = bridge_nfc_set_timing(app->nfc, control->data, control->length);
        break;
    case BridgeMsgStop:
        bridge_nfc_stop(app->nfc);
        break;
    }
    bridge_ack(app, control->type, status);
}

/* ---- UI ---- */

static const char* tech_name(uint8_t tech) {
    switch(tech) {
    case BridgeTechIso14443a:
        return "14443-A";
    case BridgeTechIso14443b:
        return "14443-B";
    case BridgeTechIso15693:
        return "15693";
    case BridgeTechFelica:
        return "FeliCa";
    default:
        return "?";
    }
}

static void bridge_draw(Canvas* canvas, void* context) {
    BridgeApp* app = context;
    BridgeView view;
    bridge_nfc_get_view(app->nfc, &view);
    char line[40];

    canvas_clear(canvas);
    canvas_set_font(canvas, FontPrimary);
    canvas_draw_str(canvas, 2, 11, "NFC Bridge");
    canvas_set_font(canvas, FontSecondary);

    if(!usb_bridge_is_connected(app->usb)) {
        canvas_draw_str(canvas, 2, 26, "Connect USB to the computer");
        canvas_draw_str(canvas, 2, 38, "and start the host app.");
    } else if(view.mode == BridgeModeOff) {
        canvas_draw_str(canvas, 2, 26, usb_bridge_is_bulk(app->usb) ? "USB ready (bulk)." : "USB ready (HID).");
        canvas_draw_str(canvas, 2, 38, "Waiting for the host.");
    } else {
        snprintf(
            line,
            sizeof(line),
            "%s %s  %s",
            view.mode == BridgeModeListen ? "Card" : "Reader",
            tech_name(view.tech),
            view.field ? "(field)" : "");
        canvas_draw_str(canvas, 2, 23, line);

        if(view.mode == BridgeModeListen) {
            size_t pos = snprintf(line, sizeof(line), "ID ");
            for(uint8_t i = 0; i < view.uid_length && pos + 2 < sizeof(line); i++) {
                pos += snprintf(&line[pos], sizeof(line) - pos, "%02X", view.uid[i]);
            }
            if(view.uid_length == 0) snprintf(line, sizeof(line), "Table entries %u", (unsigned)view.table_entries);
            canvas_draw_str(canvas, 2, 33, line);
            snprintf(
                line,
                sizeof(line),
                "rx %lu ok %lu tbl %lu t/o %lu",
                (unsigned long)view.stats.frames,
                (unsigned long)view.stats.answered,
                (unsigned long)view.stats.table_hits,
                (unsigned long)view.stats.timeouts);
            canvas_draw_str(canvas, 2, 43, line);
        } else {
            snprintf(
                line,
                sizeof(line),
                "trx %lu  t/o %lu",
                (unsigned long)view.stats.transceives,
                (unsigned long)view.stats.timeouts);
            canvas_draw_str(canvas, 2, 38, line);
        }

        canvas_draw_str(canvas, 2, 54, view.last_frame);
    }

    canvas_draw_str_aligned(canvas, 126, 63, AlignRight, AlignBottom, "Back: exit");
}

static void bridge_input(InputEvent* event, void* context) {
    BridgeApp* app = context;
    furi_message_queue_put(app->input_queue, event, FuriWaitForever);
}

/* ---- app ---- */

static BridgeApp* bridge_app_alloc(void) {
    BridgeApp* app = malloc(sizeof(BridgeApp));
    memset(app, 0, sizeof(BridgeApp));

    app->input_queue = furi_message_queue_alloc(8, sizeof(InputEvent));
    app->control_queue = furi_message_queue_alloc(4, sizeof(BridgeControl));
    app->tx_mutex = furi_mutex_alloc(FuriMutexTypeNormal);
    app->usb = usb_bridge_alloc();
    app->nfc = bridge_nfc_alloc(bridge_send, app);

    app->view_port = view_port_alloc();
    view_port_draw_callback_set(app->view_port, bridge_draw, app);
    view_port_input_callback_set(app->view_port, bridge_input, app);
    app->gui = furi_record_open(RECORD_GUI);
    gui_add_view_port(app->gui, app->view_port, GuiLayerFullscreen);

    app->running = true;
    app->rx_thread = furi_thread_alloc_ex("NfcBridgeRx", 2048, bridge_rx_thread, app);
    furi_thread_start(app->rx_thread);
    return app;
}

static void bridge_app_free(BridgeApp* app) {
    bridge_nfc_stop(app->nfc);

    app->running = false;
    furi_thread_join(app->rx_thread);
    furi_thread_free(app->rx_thread);

    gui_remove_view_port(app->gui, app->view_port);
    view_port_free(app->view_port);
    furi_record_close(RECORD_GUI);

    bridge_nfc_free(app->nfc);
    usb_bridge_free(app->usb);
    furi_mutex_free(app->tx_mutex);
    furi_message_queue_free(app->control_queue);
    furi_message_queue_free(app->input_queue);
    free(app);
}

int32_t nfc_bridge_app(void* p) {
    UNUSED(p);
    BridgeApp* app = bridge_app_alloc();

    bool was_connected = false;
    bool exit = false;
    while(!exit) {
        InputEvent input;
        if(furi_message_queue_get(app->input_queue, &input, 100) == FuriStatusOk && input.type == InputTypeShort &&
           input.key == InputKeyBack) {
            exit = true;
        }

        BridgeControl control;
        while(furi_message_queue_get(app->control_queue, &control, 0) == FuriStatusOk) {
            bridge_run_control(app, &control);
        }

        /* Unplugged: stop, so a reader doesn't see a card that can't answer (or a field nobody drives). */
        const bool connected = usb_bridge_is_connected(app->usb);
        if(was_connected && !connected) {
            bridge_nfc_stop(app->nfc);
        }
        was_connected = connected;

        bridge_micros();
        view_port_update(app->view_port);
    }

    bridge_app_free(app);
    return 0;
}
