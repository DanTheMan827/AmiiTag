#pragma once

#include <furi.h>
#include <stdbool.h>
#include <stdint.h>

/*
 * A vendor defined USB HID interface (64 byte reports each way, 1 ms interrupt endpoints) that replaces the Flipper's
 * USB CDC while the app runs. Reports from the host are queued; reports to the host are sent one at a time.
 */

typedef struct UsbBridge UsbBridge;

/* Switches the Flipper's USB to the bridge interface (the previous one is restored by usb_bridge_free). */
UsbBridge* usb_bridge_alloc(void);

void usb_bridge_free(UsbBridge* bridge);

/* True once the host has configured the device. */
bool usb_bridge_is_connected(UsbBridge* bridge);

/* Sends one 64 byte report. Waits up to timeout_ms for the previous one to go out. */
bool usb_bridge_send(UsbBridge* bridge, const uint8_t report[64], uint32_t timeout_ms);

/* Takes the next 64 byte report from the host, waiting up to timeout_ms. */
bool usb_bridge_receive(UsbBridge* bridge, uint8_t report[64], uint32_t timeout_ms);
