#include "usb_bridge.h"
#include "bridge_protocol.h"

#include <furi_hal_usb.h>
#include <furi_hal_version.h>
#include <usb.h>
#include <usb_hid.h>

/* The firmware's control endpoint size (furi_hal_usb_i.h, which apps can't include). */
#ifndef USB_EP0_SIZE
#define USB_EP0_SIZE 8
#endif

#define BRIDGE_EP_IN 0x81
#define BRIDGE_EP_OUT 0x01
#define BRIDGE_EP_SIZE 64
#define BRIDGE_RX_QUEUE 32

/* Vendor defined: 64 byte input report, 64 byte output report, no report IDs. */
static const uint8_t bridge_report_desc[] = {
    0x06, BRIDGE_HID_USAGE_PAGE & 0xFF, BRIDGE_HID_USAGE_PAGE >> 8, /* Usage Page (Vendor 0xFF41) */
    0x09, BRIDGE_HID_USAGE, /* Usage (1) */
    0xA1, 0x01, /* Collection (Application) */
    0x09, 0x02, /*   Usage (2) */
    0x15, 0x00, /*   Logical Minimum (0) */
    0x26, 0xFF, 0x00, /*   Logical Maximum (255) */
    0x75, 0x08, /*   Report Size (8) */
    0x95, BRIDGE_REPORT_SIZE, /*   Report Count (64) */
    0x81, 0x02, /*   Input (Data, Var, Abs) */
    0x09, 0x03, /*   Usage (3) */
    0x15, 0x00, /*   Logical Minimum (0) */
    0x26, 0xFF, 0x00, /*   Logical Maximum (255) */
    0x75, 0x08, /*   Report Size (8) */
    0x95, BRIDGE_REPORT_SIZE, /*   Report Count (64) */
    0x91, 0x02, /*   Output (Data, Var, Abs) */
    0xC0, /* End Collection */
};

struct BridgeConfigDescriptor {
    struct usb_config_descriptor config;
    struct usb_interface_descriptor intf;
    struct usb_hid_descriptor hid;
    struct usb_endpoint_descriptor ep_in;
    struct usb_endpoint_descriptor ep_out;
} __attribute__((packed));

static const struct usb_device_descriptor bridge_device_desc = {
    .bLength = sizeof(struct usb_device_descriptor),
    .bDescriptorType = USB_DTYPE_DEVICE,
    .bcdUSB = VERSION_BCD(2, 0, 0),
    .bDeviceClass = USB_CLASS_PER_INTERFACE,
    .bDeviceSubClass = USB_SUBCLASS_NONE,
    .bDeviceProtocol = USB_PROTO_NONE,
    .bMaxPacketSize0 = USB_EP0_SIZE,
    .idVendor = BRIDGE_USB_VID,
    .idProduct = BRIDGE_USB_PID,
    .bcdDevice = VERSION_BCD(1, 0, 0),
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = NO_DESCRIPTOR,
    .bNumConfigurations = 1,
};

static const struct BridgeConfigDescriptor bridge_config_desc = {
    .config =
        {
            .bLength = sizeof(struct usb_config_descriptor),
            .bDescriptorType = USB_DTYPE_CONFIGURATION,
            .wTotalLength = sizeof(struct BridgeConfigDescriptor),
            .bNumInterfaces = 1,
            .bConfigurationValue = 1,
            .iConfiguration = NO_DESCRIPTOR,
            .bmAttributes = USB_CFG_ATTR_RESERVED | USB_CFG_ATTR_SELFPOWERED,
            .bMaxPower = USB_CFG_POWER_MA(100),
        },
    .intf =
        {
            .bLength = sizeof(struct usb_interface_descriptor),
            .bDescriptorType = USB_DTYPE_INTERFACE,
            .bInterfaceNumber = 0,
            .bAlternateSetting = 0,
            .bNumEndpoints = 2,
            .bInterfaceClass = USB_CLASS_HID,
            .bInterfaceSubClass = USB_HID_SUBCLASS_NONBOOT,
            .bInterfaceProtocol = USB_HID_PROTO_NONBOOT,
            .iInterface = NO_DESCRIPTOR,
        },
    .hid =
        {
            .bLength = sizeof(struct usb_hid_descriptor),
            .bDescriptorType = USB_DTYPE_HID,
            .bcdHID = VERSION_BCD(1, 1, 1),
            .bCountryCode = USB_HID_COUNTRY_NONE,
            .bNumDescriptors = 1,
            .bDescriptorType0 = USB_DTYPE_HID_REPORT,
            .wDescriptorLength0 = sizeof(bridge_report_desc),
        },
    .ep_in =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = BRIDGE_EP_IN,
            .bmAttributes = USB_EPTYPE_INTERRUPT,
            .wMaxPacketSize = BRIDGE_EP_SIZE,
            .bInterval = 1,
        },
    .ep_out =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = BRIDGE_EP_OUT,
            .bmAttributes = USB_EPTYPE_INTERRUPT,
            .wMaxPacketSize = BRIDGE_EP_SIZE,
            .bInterval = 1,
        },
};

static const struct usb_string_descriptor bridge_manufacturer = USB_STRING_DESC("NFC Bridge");
static const struct usb_string_descriptor bridge_product = USB_STRING_DESC("NFC Bridge"); /* = BRIDGE_USB_PRODUCT */

struct UsbBridge {
    FuriHalUsbInterface* previous;
    FuriSemaphore* tx_free;
    FuriMessageQueue* rx_queue;
    volatile bool configured;
    volatile bool suspended;
};

/* libusb_stm32 callbacks have no context pointer: one bridge at a time. */
static UsbBridge* bridge_instance = NULL;
static usbd_device* bridge_dev = NULL;

static void bridge_init(usbd_device* dev, FuriHalUsbInterface* intf, void* ctx);
static void bridge_deinit(usbd_device* dev);
static void bridge_on_wakeup(usbd_device* dev);
static void bridge_on_suspend(usbd_device* dev);

static FuriHalUsbInterface bridge_interface = {
    .init = bridge_init,
    .deinit = bridge_deinit,
    .wakeup = bridge_on_wakeup,
    .suspend = bridge_on_suspend,
    .dev_descr = (struct usb_device_descriptor*)&bridge_device_desc,
    .str_manuf_descr = (void*)&bridge_manufacturer,
    .str_prod_descr = (void*)&bridge_product,
    .str_serial_descr = NULL,
    .cfg_descr = (void*)&bridge_config_desc,
};

static void bridge_ep_callback(usbd_device* dev, uint8_t event, uint8_t ep) {
    UNUSED(ep);
    UsbBridge* bridge = bridge_instance;
    if(bridge == NULL) {
        return;
    }

    if(event == usbd_evt_eptx) {
        furi_semaphore_release(bridge->tx_free);
    } else if(event == usbd_evt_eprx) {
        uint8_t report[BRIDGE_REPORT_SIZE] = {0};
        int32_t length = usbd_ep_read(dev, BRIDGE_EP_OUT, report, sizeof(report));
        if(length > 0) {
            /* Interrupt context: never wait. A full queue drops the report (the host retries on timeout). */
            furi_message_queue_put(bridge->rx_queue, report, 0);
        }
    }
}

static usbd_respond bridge_ep_config(usbd_device* dev, uint8_t cfg) {
    UsbBridge* bridge = bridge_instance;
    switch(cfg) {
    case 0:
        usbd_ep_deconfig(dev, BRIDGE_EP_IN);
        usbd_ep_deconfig(dev, BRIDGE_EP_OUT);
        usbd_reg_endpoint(dev, BRIDGE_EP_IN, 0);
        usbd_reg_endpoint(dev, BRIDGE_EP_OUT, 0);
        if(bridge) bridge->configured = false;
        return usbd_ack;
    case 1:
        usbd_ep_config(dev, BRIDGE_EP_IN, USB_EPTYPE_INTERRUPT, BRIDGE_EP_SIZE);
        usbd_ep_config(dev, BRIDGE_EP_OUT, USB_EPTYPE_INTERRUPT, BRIDGE_EP_SIZE);
        usbd_reg_endpoint(dev, BRIDGE_EP_IN, bridge_ep_callback);
        usbd_reg_endpoint(dev, BRIDGE_EP_OUT, bridge_ep_callback);
        if(bridge) {
            bridge->configured = true;
            bridge->suspended = false;
        }
        return usbd_ack;
    default:
        return usbd_fail;
    }
}

static usbd_respond bridge_control(usbd_device* dev, usbd_ctlreq* req, usbd_rqc_callback* callback) {
    UNUSED(callback);
    const uint8_t recipient_type = req->bmRequestType & (USB_REQ_RECIPIENT | USB_REQ_TYPE);

    if(recipient_type == (USB_REQ_INTERFACE | USB_REQ_CLASS) && req->wIndex == 0) {
        switch(req->bRequest) {
        case USB_HID_SETIDLE:
            return usbd_ack;
        case USB_HID_GETREPORT: {
            static uint8_t empty[BRIDGE_REPORT_SIZE];
            dev->status.data_ptr = empty;
            dev->status.data_count = sizeof(empty);
            return usbd_ack;
        }
        default:
            return usbd_fail;
        }
    }

    if(recipient_type == (USB_REQ_INTERFACE | USB_REQ_STANDARD) && req->wIndex == 0 &&
       req->bRequest == USB_STD_GET_DESCRIPTOR) {
        switch(req->wValue >> 8) {
        case USB_DTYPE_HID:
            dev->status.data_ptr = (uint8_t*)&bridge_config_desc.hid;
            dev->status.data_count = sizeof(bridge_config_desc.hid);
            return usbd_ack;
        case USB_DTYPE_HID_REPORT:
            dev->status.data_ptr = (uint8_t*)bridge_report_desc;
            dev->status.data_count = sizeof(bridge_report_desc);
            return usbd_ack;
        default:
            return usbd_fail;
        }
    }

    return usbd_fail;
}

static void bridge_init(usbd_device* dev, FuriHalUsbInterface* intf, void* ctx) {
    UNUSED(intf);
    UNUSED(ctx);
    bridge_dev = dev;
    usbd_reg_config(dev, bridge_ep_config);
    usbd_reg_control(dev, bridge_control);
    usbd_connect(dev, true);
}

static void bridge_deinit(usbd_device* dev) {
    usbd_reg_config(dev, NULL);
    usbd_reg_control(dev, NULL);
    bridge_dev = NULL;
}

static void bridge_on_wakeup(usbd_device* dev) {
    UNUSED(dev);
    if(bridge_instance) bridge_instance->suspended = false;
}

static void bridge_on_suspend(usbd_device* dev) {
    UNUSED(dev);
    if(bridge_instance) bridge_instance->suspended = true;
}

UsbBridge* usb_bridge_alloc(void) {
    UsbBridge* bridge = malloc(sizeof(UsbBridge));
    bridge->tx_free = furi_semaphore_alloc(1, 1);
    bridge->rx_queue = furi_message_queue_alloc(BRIDGE_RX_QUEUE, BRIDGE_REPORT_SIZE);
    bridge->configured = false;
    bridge->suspended = false;
    bridge_instance = bridge;

    bridge->previous = furi_hal_usb_get_config();
    furi_hal_usb_unlock();
    furi_check(furi_hal_usb_set_config(&bridge_interface, NULL));
    return bridge;
}

void usb_bridge_free(UsbBridge* bridge) {
    furi_hal_usb_set_config(bridge->previous, NULL);
    bridge_instance = NULL;
    furi_message_queue_free(bridge->rx_queue);
    furi_semaphore_free(bridge->tx_free);
    free(bridge);
}

bool usb_bridge_is_connected(UsbBridge* bridge) {
    return bridge->configured && !bridge->suspended && bridge_dev != NULL;
}

bool usb_bridge_send(UsbBridge* bridge, const uint8_t report[64], uint32_t timeout_ms) {
    if(!usb_bridge_is_connected(bridge)) {
        return false;
    }

    if(furi_semaphore_acquire(bridge->tx_free, furi_ms_to_ticks(timeout_ms)) != FuriStatusOk) {
        return false;
    }

    if(usbd_ep_write(bridge_dev, BRIDGE_EP_IN, (void*)report, BRIDGE_REPORT_SIZE) < 0) {
        furi_semaphore_release(bridge->tx_free);
        return false;
    }

    return true;
}

bool usb_bridge_receive(UsbBridge* bridge, uint8_t report[64], uint32_t timeout_ms) {
    return furi_message_queue_get(bridge->rx_queue, report, furi_ms_to_ticks(timeout_ms)) == FuriStatusOk;
}
