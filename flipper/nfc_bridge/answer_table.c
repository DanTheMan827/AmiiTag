#include "answer_table.h"
#include "bridge_protocol.h"

#include <stdlib.h>
#include <string.h>

/* Entries packed one after another: u8 flags, u8 match length, u16 answer bits, match, answer, [parity]. */
#define ENTRY_HEADER 4

struct AnswerTable {
    uint8_t* arena;
    size_t capacity;
    size_t used;
    size_t count;
};

static size_t answer_bytes(uint16_t bits) {
    return (bits + 7) / 8;
}

static size_t entry_size(const uint8_t* entry) {
    const uint16_t bits = entry[2] | (entry[3] << 8);
    const size_t bytes = answer_bytes(bits);
    const size_t parity = (entry[0] & BridgeTxCustomParity) ? (bytes + 7) / 8 : 0;
    return ENTRY_HEADER + entry[1] + bytes + parity;
}

static uint8_t* find_entry(const AnswerTable* table, const uint8_t* match, size_t length) {
    size_t offset = 0;
    while(offset < table->used) {
        uint8_t* entry = &table->arena[offset];
        if(entry[1] == length && memcmp(&entry[ENTRY_HEADER], match, length) == 0) {
            return entry;
        }
        offset += entry_size(entry);
    }
    return NULL;
}

AnswerTable* answer_table_alloc(size_t capacity_bytes) {
    AnswerTable* table = malloc(sizeof(AnswerTable));
    table->arena = malloc(capacity_bytes);
    table->capacity = capacity_bytes;
    table->used = 0;
    table->count = 0;
    return table;
}

void answer_table_free(AnswerTable* table) {
    free(table->arena);
    free(table);
}

void answer_table_clear(AnswerTable* table) {
    table->used = 0;
    table->count = 0;
}

size_t answer_table_count(const AnswerTable* table) {
    return table->count;
}

bool answer_table_remove(AnswerTable* table, const uint8_t* match, uint8_t match_length) {
    uint8_t* entry = find_entry(table, match, match_length);
    if(entry == NULL) {
        return false;
    }

    const size_t size = entry_size(entry);
    const size_t offset = entry - table->arena;
    memmove(entry, entry + size, table->used - offset - size);
    table->used -= size;
    table->count--;
    return true;
}

bool answer_table_set(
    AnswerTable* table,
    uint8_t flags,
    const uint8_t* match,
    uint8_t match_length,
    uint16_t answer_bits,
    const uint8_t* answer,
    const uint8_t* parity) {
    if(match_length == 0) {
        return false;
    }

    answer_table_remove(table, match, match_length);

    const size_t bytes = answer_bytes(answer_bits);
    const size_t parity_bytes = (flags & BridgeTxCustomParity) ? (bytes + 7) / 8 : 0;
    const size_t size = ENTRY_HEADER + match_length + bytes + parity_bytes;
    if(table->used + size > table->capacity) {
        return false;
    }

    uint8_t* entry = &table->arena[table->used];
    entry[0] = flags;
    entry[1] = match_length;
    entry[2] = answer_bits & 0xFF;
    entry[3] = answer_bits >> 8;
    memcpy(&entry[ENTRY_HEADER], match, match_length);
    memcpy(&entry[ENTRY_HEADER + match_length], answer, bytes);
    if(parity_bytes) {
        memcpy(&entry[ENTRY_HEADER + match_length + bytes], parity, parity_bytes);
    }

    table->used += size;
    table->count++;
    return true;
}

bool answer_table_find(const AnswerTable* table, const uint8_t* frame, size_t length, AnswerTableHit* hit) {
    const uint8_t* entry = find_entry(table, frame, length);
    if(entry == NULL) {
        return false;
    }

    hit->flags = entry[0];
    hit->bits = entry[2] | (entry[3] << 8);
    hit->data = &entry[ENTRY_HEADER + entry[1]];
    hit->parity = (hit->flags & BridgeTxCustomParity) ? hit->data + answer_bytes(hit->bits) : NULL;
    return true;
}
