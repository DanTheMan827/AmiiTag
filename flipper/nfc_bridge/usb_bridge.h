#pragma once

#include <furi.h>
#include <stdbool.h>
#include <stdint.h>

/*
 * The bridge's USB device, replacing the Flipper's USB CDC while the app runs. Two interfaces carry the same 64 byte
 * packets:
 *   0: vendor defined HID (64 byte reports, 1 ms interrupt endpoints): driverless everywhere, browsers included,
 *      but one packet per millisecond each way.
 *   1: vendor bulk (64 byte packets, as many per millisecond as the host schedules): bound to WinUSB on Windows
 *      automatically (Microsoft OS 2.0 descriptors), libusb/WebUSB elsewhere.
 * Packets to the host go out on the interface the host last sent on.
 */

typedef struct UsbBridge UsbBridge;

/* Switches the Flipper's USB to the bridge (the previous configuration is restored by usb_bridge_free). */
UsbBridge* usb_bridge_alloc(void);

void usb_bridge_free(UsbBridge* bridge);

/* True once the host has configured the device. */
bool usb_bridge_is_connected(UsbBridge* bridge);

/* True when the host talks over the bulk interface. */
bool usb_bridge_is_bulk(UsbBridge* bridge);

/* Sends one 64 byte packet. Waits up to timeout_ms for the previous one to go out. */
bool usb_bridge_send(UsbBridge* bridge, const uint8_t report[64], uint32_t timeout_ms);

/* Takes the next 64 byte packet from the host, waiting up to timeout_ms. */
bool usb_bridge_receive(UsbBridge* bridge, uint8_t report[64], uint32_t timeout_ms);
