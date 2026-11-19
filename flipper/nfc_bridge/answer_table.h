#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * Answers the Flipper sends on its own, within microseconds, for exact frame matches: the way to satisfy readers
 * whose timing a USB round trip can't meet. Not thread safe: the caller locks.
 */

typedef struct AnswerTable AnswerTable;

typedef struct {
    uint8_t flags; /* BridgeTxFlag */
    uint16_t bits;
    const uint8_t* data;
    const uint8_t* parity; /* NULL unless BridgeTxCustomParity */
} AnswerTableHit;

AnswerTable* answer_table_alloc(size_t capacity_bytes);
void answer_table_free(AnswerTable* table);
void answer_table_clear(AnswerTable* table);
size_t answer_table_count(const AnswerTable* table);

/* Adds or replaces the answer for this exact frame (CRC already stripped if the listener strips it). */
bool answer_table_set(
    AnswerTable* table,
    uint8_t flags,
    const uint8_t* match,
    uint8_t match_length,
    uint16_t answer_bits,
    const uint8_t* answer,
    const uint8_t* parity);

bool answer_table_remove(AnswerTable* table, const uint8_t* match, uint8_t match_length);

/* The hit points into the table: use it before the next change. */
bool answer_table_find(const AnswerTable* table, const uint8_t* frame, size_t length, AnswerTableHit* hit);
