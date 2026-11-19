#include "usb_bridge.h"
#include "bridge_protocol.h"

#include <furi_hal_usb.h>
#include <usb.h>
#include <usb_hid.h>

/* The firmware's control endpoint size (furi_hal_usb_i.h, which apps can't include). */
#ifndef USB_EP0_SIZE
#define USB_EP0_SIZE 8
#endif

#define HID_INTERFACE 0
#define BULK_INTERFACE 1
#define HID_EP_IN 0x81
#define HID_EP_OUT 0x01
#define BULK_EP_IN 0x82
#define BULK_EP_OUT 0x02
#define EP_SIZE 64
#define RX_QUEUE 64

#define USB_DTYPE_BOS 0x0F
#define MS_OS_20_VENDOR_CODE 0x20
#define MS_OS_20_DESCRIPTOR_INDEX 0x07

/* ---- descriptors ---- */

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
    struct usb_interface_descriptor hid_intf;
    struct usb_hid_descriptor hid;
    struct usb_endpoint_descriptor hid_in;
    struct usb_endpoint_descriptor hid_out;
    struct usb_interface_descriptor bulk_intf;
    struct usb_endpoint_descriptor bulk_in;
    struct usb_endpoint_descriptor bulk_out;
} __attribute__((packed));

/* USB 2.01: tells Windows to read the BOS descriptor (and so the Microsoft OS 2.0 descriptors). */
static const struct usb_device_descriptor bridge_device_desc = {
    .bLength = sizeof(struct usb_device_descriptor),
    .bDescriptorType = USB_DTYPE_DEVICE,
    .bcdUSB = VERSION_BCD(2, 0, 1),
    .bDeviceClass = USB_CLASS_PER_INTERFACE,
    .bDeviceSubClass = USB_SUBCLASS_NONE,
    .bDeviceProtocol = USB_PROTO_NONE,
    .bMaxPacketSize0 = USB_EP0_SIZE,
    .idVendor = BRIDGE_USB_VID,
    .idProduct = BRIDGE_USB_PID,
    /* Windows caches descriptors per VID/PID/revision: a new revision makes it read the new interfaces. */
    .bcdDevice = VERSION_BCD(2, 1, 0),
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
            .bNumInterfaces = 2,
            .bConfigurationValue = 1,
            .iConfiguration = NO_DESCRIPTOR,
            .bmAttributes = USB_CFG_ATTR_RESERVED | USB_CFG_ATTR_SELFPOWERED,
            .bMaxPower = USB_CFG_POWER_MA(100),
        },
    .hid_intf =
        {
            .bLength = sizeof(struct usb_interface_descriptor),
            .bDescriptorType = USB_DTYPE_INTERFACE,
            .bInterfaceNumber = HID_INTERFACE,
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
    .hid_in =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = HID_EP_IN,
            .bmAttributes = USB_EPTYPE_INTERRUPT,
            .wMaxPacketSize = EP_SIZE,
            .bInterval = 1,
        },
    .hid_out =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = HID_EP_OUT,
            .bmAttributes = USB_EPTYPE_INTERRUPT,
            .wMaxPacketSize = EP_SIZE,
            .bInterval = 1,
        },
    .bulk_intf =
        {
            .bLength = sizeof(struct usb_interface_descriptor),
            .bDescriptorType = USB_DTYPE_INTERFACE,
            .bInterfaceNumber = BULK_INTERFACE,
            .bAlternateSetting = 0,
            .bNumEndpoints = 2,
            .bInterfaceClass = USB_CLASS_VENDOR,
            .bInterfaceSubClass = USB_SUBCLASS_NONE,
            .bInterfaceProtocol = USB_PROTO_NONE,
            .iInterface = NO_DESCRIPTOR,
        },
    .bulk_in =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = BULK_EP_IN,
            .bmAttributes = USB_EPTYPE_BULK,
            .wMaxPacketSize = EP_SIZE,
            .bInterval = 0,
        },
    .bulk_out =
        {
            .bLength = sizeof(struct usb_endpoint_descriptor),
            .bDescriptorType = USB_DTYPE_ENDPOINT,
            .bEndpointAddress = BULK_EP_OUT,
            .bmAttributes = USB_EPTYPE_BULK,
            .wMaxPacketSize = EP_SIZE,
            .bInterval = 0,
        },
};

/*
 * Microsoft OS 2.0 descriptor set: interface 1 is WinUSB compatible, with device interface GUID
 * BRIDGE_WINUSB_GUID, so Windows (8.1 and later) binds its own WinUSB driver to it: nothing to install.
 */
static const uint8_t bridge_msos20_desc[] = {
    0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x06, 0xB2, 0x00, 0x08, 0x00,
    0x01, 0x00, 0x00, 0x00, 0xA8, 0x00, 0x08, 0x00, 0x02, 0x00, 0x01, 0x00,
    0xA0, 0x00, 0x14, 0x00, 0x03, 0x00, 0x57, 0x49, 0x4E, 0x55, 0x53, 0x42,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x84, 0x00,
    0x04, 0x00, 0x07, 0x00, 0x2A, 0x00, 0x44, 0x00, 0x65, 0x00, 0x76, 0x00,
    0x69, 0x00, 0x63, 0x00, 0x65, 0x00, 0x49, 0x00, 0x6E, 0x00, 0x74, 0x00,
    0x65, 0x00, 0x72, 0x00, 0x66, 0x00, 0x61, 0x00, 0x63, 0x00, 0x65, 0x00,
    0x47, 0x00, 0x55, 0x00, 0x49, 0x00, 0x44, 0x00, 0x73, 0x00, 0x00, 0x00,
    0x50, 0x00, 0x7B, 0x00, 0x43, 0x00, 0x34, 0x00, 0x41, 0x00, 0x31, 0x00,
    0x44, 0x00, 0x37, 0x00, 0x45, 0x00, 0x32, 0x00, 0x2D, 0x00, 0x35, 0x00,
    0x42, 0x00, 0x33, 0x00, 0x46, 0x00, 0x2D, 0x00, 0x34, 0x00, 0x45, 0x00,
    0x38, 0x00, 0x38, 0x00, 0x2D, 0x00, 0x39, 0x00, 0x44, 0x00, 0x32, 0x00,
    0x31, 0x00, 0x2D, 0x00, 0x36, 0x00, 0x46, 0x00, 0x30, 0x00, 0x41, 0x00,
    0x33, 0x00, 0x42, 0x00, 0x37, 0x00, 0x43, 0x00, 0x39, 0x00, 0x45, 0x00,
    0x31, 0x00, 0x34, 0x00, 0x7D, 0x00, 0x00, 0x00, 0x00, 0x00,
};

/* BOS descriptor with the Microsoft OS 2.0 platform capability, pointing at the set above. */
static const uint8_t bridge_bos_desc[] = {
    0x05, USB_DTYPE_BOS, 0x21, 0x00, 0x01, /* BOS: total length 33, one capability */
    0x1C, 0x10, 0x05, 0x00, /* Device capability: platform, 28 bytes */
    0xDF, 0x60, 0xDD, 0xD8, 0x89, 0x45, 0xC7, 0x4C, /* MS OS 2.0 platform capability UUID */
    0x9C, 0xD2, 0x65, 0x9D, 0x9E, 0x64, 0x8A, 0x9F, /* {D8DD60DF-4589-4CC7-9CD2-659D9E648A9F} */
    0x00, 0x00, 0x03, 0x06, /* Windows 8.1 and later */
    sizeof(bridge_msos20_desc) & 0xFF, sizeof(bridge_msos20_desc) >> 8, /* descriptor set length */
    MS_OS_20_VENDOR_CODE, /* vendor request that returns it */
    0x00, /* no alternate enumeration */
};

_Static_assert(sizeof(bridge_bos_desc) == 33, "BOS descriptor length");
_Static_assert(sizeof(bridge_msos20_desc) == 178, "MS OS 2.0 descriptor set length");

static const struct usb_string_descriptor bridge_manufacturer = USB_STRING_DESC("NFC Bridge");
static const struct usb_string_descriptor bridge_product = USB_STRING_DESC("NFC Bridge"); /* = BRIDGE_USB_PRODUCT */

/* ---- state ---- */

struct UsbBridge {
    FuriHalUsbInterface* previous;
    FuriSemaphore* hid_tx_free;
    FuriSemaphore* bulk_tx_free;
    FuriMessageQueue* rx_queue;
    volatile bool configured;
    volatile bool suspended;
    volatile bool bulk; /* the host last sent on the bulk interface */
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

/* ---- endpoints ---- */

static void bridge_ep_callback(usbd_device* dev, uint8_t event, uint8_t ep) {
    UsbBridge* bridge = bridge_instance;
    if(bridge == NULL) {
        return;
    }

    if(event == usbd_evt_eptx) {
        furi_semaphore_release((ep & 0x7F) == (BULK_EP_IN & 0x7F) ? bridge->bulk_tx_free : bridge->hid_tx_free);
    } else if(event == usbd_evt_eprx) {
        uint8_t report[BRIDGE_REPORT_SIZE] = {0};
        int32_t length = usbd_ep_read(dev, ep, report, sizeof(report));
        if(length > 0) {
            bridge->bulk = (ep & 0x7F) == BULK_EP_OUT;
            /* Interrupt context: never wait. A full queue drops the packet (the host retries on timeout). */
            furi_message_queue_put(bridge->rx_queue, report, 0);
        }
    }
}

static void bridge_ep_set(usbd_device* dev, uint8_t ep, uint8_t type, bool on) {
    if(on) {
        usbd_ep_config(dev, ep, type, EP_SIZE);
        usbd_reg_endpoint(dev, ep, bridge_ep_callback);
    } else {
        usbd_ep_deconfig(dev, ep);
        usbd_reg_endpoint(dev, ep, 0);
    }
}

static usbd_respond bridge_ep_config(usbd_device* dev, uint8_t cfg) {
    UsbBridge* bridge = bridge_instance;
    if(cfg > 1) {
        return usbd_fail;
    }

    const bool on = cfg == 1;
    bridge_ep_set(dev, HID_EP_IN, USB_EPTYPE_INTERRUPT, on);
    bridge_ep_set(dev, HID_EP_OUT, USB_EPTYPE_INTERRUPT, on);
    bridge_ep_set(dev, BULK_EP_IN, USB_EPTYPE_BULK, on);
    bridge_ep_set(dev, BULK_EP_OUT, USB_EPTYPE_BULK, on);
    if(bridge) {
        bridge->configured = on;
        bridge->suspended = false;
    }
    return usbd_ack;
}

/* ---- control requests ---- */

static usbd_respond bridge_reply(usbd_device* dev, usbd_ctlreq* req, const void* data, uint16_t length) {
    dev->status.data_ptr = (void*)data;
    dev->status.data_count = MIN(length, req->wLength);
    return usbd_ack;
}

static usbd_respond bridge_control(usbd_device* dev, usbd_ctlreq* req, usbd_rqc_callback* callback) {
    UNUSED(callback);
    const uint8_t type = req->bmRequestType & (USB_REQ_TYPE | USB_REQ_RECIPIENT);

    /* Windows asks for the BOS descriptor (bcdUSB 2.01), then for the Microsoft OS 2.0 descriptor set. */
    if(type == (USB_REQ_STANDARD | USB_REQ_DEVICE) && req->bRequest == USB_STD_GET_DESCRIPTOR &&
       (req->wValue >> 8) == USB_DTYPE_BOS) {
        return bridge_reply(dev, req, bridge_bos_desc, sizeof(bridge_bos_desc));
    }

    if(type == (USB_REQ_VENDOR | USB_REQ_DEVICE) && req->bRequest == MS_OS_20_VENDOR_CODE &&
       req->wIndex == MS_OS_20_DESCRIPTOR_INDEX) {
        return bridge_reply(dev, req, bridge_msos20_desc, sizeof(bridge_msos20_desc));
    }

    if((req->wIndex & 0xFF) != HID_INTERFACE) {
        return usbd_fail;
    }

    if(type == (USB_REQ_INTERFACE | USB_REQ_CLASS)) {
        switch(req->bRequest) {
        case USB_HID_SETIDLE:
            return usbd_ack;
        case USB_HID_GETREPORT: {
            static uint8_t empty[BRIDGE_REPORT_SIZE];
            return bridge_reply(dev, req, empty, sizeof(empty));
        }
        default:
            return usbd_fail;
        }
    }

    if(type == (USB_REQ_INTERFACE | USB_REQ_STANDARD) && req->bRequest == USB_STD_GET_DESCRIPTOR) {
        switch(req->wValue >> 8) {
        case USB_DTYPE_HID:
            return bridge_reply(dev, req, &bridge_config_desc.hid, sizeof(bridge_config_desc.hid));
        case USB_DTYPE_HID_REPORT:
            return bridge_reply(dev, req, bridge_report_desc, sizeof(bridge_report_desc));
        default:
            return usbd_fail;
        }
    }

    return usbd_fail;
}

/* ---- interface lifecycle ---- */

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
    bridge->hid_tx_free = furi_semaphore_alloc(1, 1);
    bridge->bulk_tx_free = furi_semaphore_alloc(1, 1);
    bridge->rx_queue = furi_message_queue_alloc(RX_QUEUE, BRIDGE_REPORT_SIZE);
    bridge->configured = false;
    bridge->suspended = false;
    bridge->bulk = false;
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
    furi_semaphore_free(bridge->bulk_tx_free);
    furi_semaphore_free(bridge->hid_tx_free);
    free(bridge);
}

bool usb_bridge_is_connected(UsbBridge* bridge) {
    return bridge->configured && !bridge->suspended && bridge_dev != NULL;
}

bool usb_bridge_is_bulk(UsbBridge* bridge) {
    return bridge->bulk;
}

bool usb_bridge_send(UsbBridge* bridge, const uint8_t report[64], uint32_t timeout_ms) {
    if(!usb_bridge_is_connected(bridge)) {
        return false;
    }

    const bool bulk = bridge->bulk;
    FuriSemaphore* tx_free = bulk ? bridge->bulk_tx_free : bridge->hid_tx_free;
    if(furi_semaphore_acquire(tx_free, furi_ms_to_ticks(timeout_ms)) != FuriStatusOk) {
        return false;
    }

    if(usbd_ep_write(bridge_dev, bulk ? BULK_EP_IN : HID_EP_IN, (void*)report, BRIDGE_REPORT_SIZE) < 0) {
        furi_semaphore_release(tx_free);
        return false;
    }

    return true;
}

bool usb_bridge_receive(UsbBridge* bridge, uint8_t report[64], uint32_t timeout_ms) {
    return furi_message_queue_get(bridge->rx_queue, report, furi_ms_to_ticks(timeout_ms)) == FuriStatusOk;
}
