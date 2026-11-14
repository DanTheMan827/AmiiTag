#include "bridge_crc.h"
#include "bridge_protocol.h"

static uint16_t crc_reflected(uint16_t crc, const uint8_t* data, size_t length) {
    for(size_t i = 0; i < length; i++) {
        crc ^= data[i];
        for(int bit = 0; bit < 8; bit++) {
            crc = (crc & 1) ? (crc >> 1) ^ 0x8408 : crc >> 1;
        }
    }
    return crc;
}

static uint16_t crc_ccitt(const uint8_t* data, size_t length) {
    uint16_t crc = 0x0000;
    for(size_t i = 0; i < length; i++) {
        crc ^= (uint16_t)data[i] << 8;
        for(int bit = 0; bit < 8; bit++) {
            crc = (crc & 0x8000) ? (crc << 1) ^ 0x1021 : crc << 1;
        }
    }
    return crc;
}

void bridge_crc_compute(uint8_t tech, const uint8_t* data, size_t length, uint8_t out[2]) {
    uint16_t crc;
    switch(tech) {
    case BridgeTechIso14443a:
        crc = crc_reflected(0x6363, data, length);
        out[0] = crc & 0xFF;
        out[1] = crc >> 8;
        break;
    case BridgeTechFelica:
        crc = crc_ccitt(data, length);
        out[0] = crc >> 8;
        out[1] = crc & 0xFF;
        break;
    case BridgeTechIso14443b:
    case BridgeTechIso15693:
    default:
        crc = ~crc_reflected(0xFFFF, data, length);
        out[0] = crc & 0xFF;
        out[1] = crc >> 8;
        break;
    }
}

bool bridge_crc_check(uint8_t tech, const uint8_t* frame, size_t length) {
    if(length < 3) {
        return false;
    }
    uint8_t crc[2];
    bridge_crc_compute(tech, frame, length - 2, crc);
    return frame[length - 2] == crc[0] && frame[length - 1] == crc[1];
}
