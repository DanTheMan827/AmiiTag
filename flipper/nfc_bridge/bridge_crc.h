#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * The CRC of each technology, as transmitted (2 bytes, in air order):
 *   ISO 14443-A: CRC_A (ISO 14443-3), initial 0x6363, LSB first.
 *   ISO 14443-B and ISO 15693: the ISO/IEC 13239 CRC, initial 0xFFFF, inverted, LSB first.
 *   FeliCa: CRC-16/CCITT, initial 0x0000, MSB first (computed over the length byte and the data).
 */

void bridge_crc_compute(uint8_t tech, const uint8_t* data, size_t length, uint8_t out[2]);

/* True when the last 2 bytes are the CRC of the bytes before them. */
bool bridge_crc_check(uint8_t tech, const uint8_t* frame, size_t length);
