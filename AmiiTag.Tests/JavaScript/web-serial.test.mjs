import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../../AmiiTag.WebAssembly/wwwroot/js/amiitag.js', import.meta.url), 'utf8');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function session(onWrite, callback = async () => {}) {
    const queue = [];
    let waiting;
    let readerReleased = false;
    let writerLocked = false;
    const push = value => {
        const item = { value: Uint8Array.from(value), done: false };
        if (waiting) { const resume = waiting; waiting = null; resume(item); }
        else queue.push(item);
    };
    const reader = {
        read: () => queue.length ? Promise.resolve(queue.shift()) : new Promise(resolve => { waiting = resolve; }),
        cancel: async () => { if (waiting) { const resume = waiting; waiting = null; resume({ done: true }); } },
        releaseLock: () => { readerReleased = true; }
    };
    const port = {
        getInfo: () => ({ usbVendorId: 1234 }),
        open: async () => {},
        readable: { getReader: () => reader },
        writable: { getWriter: () => {
            assert.equal(writerLocked, false);
            writerLocked = true;
            return { write: bytes => onWrite(bytes, push), releaseLock: () => { writerLocked = false; } };
        } },
        close: async () => { assert.equal(readerReleased, true); assert.equal(writerLocked, false); }
    };
    const context = vm.createContext({ window: {}, navigator: { serial: { getPorts: async () => [port] } },
        setTimeout, clearTimeout, Uint8Array, console });
    vm.runInContext(source, context);
    const api = context.window.amiitag.serial;
    const [{ id }] = await api.getPorts();
    await api.open(id, 115200, { invokeMethodAsync: callback });
    return { api, id, push, close: () => api.close(id) };
}

test('collects fragmented USB replies without waiting for a managed callback', async () => {
    let callbacks = 0;
    const s = await session(async (_, push) => { push([1]); queueMicrotask(() => push([2, 3])); },
        method => {
            if (method === 'OnData') { callbacks++; return new Promise(() => {}); }
            return Promise.resolve();
        });
    try {
        const result = await s.api.transact(s.id, Uint8Array.of(0x80), 3, 100);
        assert.equal(result.error, null);
        assert.deepEqual(Array.from(result.data), [1, 2, 3]);
        assert.equal(callbacks, 0);
    } finally { await s.close(); }
});

test('accepts replies arriving before the serial write promise resolves', async () => {
    let writeFinished = false;
    const s = await session(async (_, push) => { push([0x42]); await delay(5); writeFinished = true; });
    try {
        const result = await s.api.transact(s.id, Uint8Array.of(0x80), 1, 100);
        assert.equal(writeFinished, true);
        assert.equal(result.data[0], 0x42);
    } finally { await s.close(); }
});

test('drains late partial replies before the next request starts', async () => {
    let batch = 0;
    const s = await session(async (_, push) => {
        if (++batch === 1) { push([0xAA]); setTimeout(() => push([0xBB]), 20); }
        else push([0xCC, 0xDD]);
    });
    try {
        const first = await s.api.transact(s.id, Uint8Array.of(0x80), 2, 10);
        assert.equal(first.error, 'timeout');
        assert.equal(first.received, 1);
        const next = await s.api.transact(s.id, Uint8Array.of(0x80), 2, 100);
        assert.deepEqual(Array.from(next.data), [0xCC, 0xDD]);
    } finally { await s.close(); }
});

test('keeps exclusive ownership while a failed batch is draining', async () => {
    const s = await session(async () => {});
    try {
        const pending = s.api.transact(s.id, Uint8Array.of(0x80), 1, 5);
        await delay(10);
        await assert.rejects(s.api.transact(s.id, Uint8Array.of(0x80), 1, 5), /already in progress/);
        assert.equal((await pending).error, 'timeout');
    } finally { await s.close(); }
});

test('closing the port finishes the outstanding batch and releases the reader', async () => {
    const s = await session(async () => {});
    const pending = s.api.transact(s.id, Uint8Array.of(0x80), 1, 1000);
    await delay(1);
    await s.close();
    assert.equal((await pending).error, 'closed');
});

test('rejects an oversized reply rather than feeding its tail to the next batch', async () => {
    let callbacks = 0;
    const s = await session(async (_, push) => push([1, 2, 3]), async () => { callbacks++; });
    try {
        const result = await s.api.transact(s.id, Uint8Array.of(0x80), 2, 100);
        assert.notEqual(result.error, null);
        assert.equal(callbacks, 0);
    } finally { await s.close(); }
});

test('many UART batches require no per-chunk managed callbacks', async () => {
    let callbacks = 0;
    const s = await session(async (request, push) => {
        for (const b of request) push([b]);
    }, async () => { callbacks++; await delay(50); });
    try {
        for (let i = 0; i < 64; i++) {
            const request = Uint8Array.from({ length: 32 }, (_, b) => b);
            const response = await s.api.transact(s.id, request, request.length, 100);
            assert.deepEqual(Array.from(response.data), Array.from(request));
        }
        assert.equal(callbacks, 0);
    } finally { await s.close(); }
});

test('raw serial reads still go to the managed channel outside a transaction', async () => {
    const data = [];
    const s = await session(async (_, push) => push([9, 8]), async (method, bytes) => {
        if (method === 'OnData') data.push(...bytes);
    });
    try {
        await s.api.write(s.id, Uint8Array.of(1));
        await delay(1);
        assert.deepEqual(data, [9, 8]);
    } finally { await s.close(); }
});

test('a failed write drains its late reply before releasing the transaction', async () => {
    let writes = 0;
    let callbacks = 0;
    const s = await session(async (_, push) => {
        if (++writes === 1) {
            setTimeout(() => push([0xAA]), 5);
            throw new Error('write rejected');
        }
        push([0xCC, 0xDD]);
    }, async method => { if (method === 'OnData') callbacks++; });
    try {
        const failed = await s.api.transact(s.id, Uint8Array.of(0x80), 2, 100);
        assert.match(failed.error, /write failed: write rejected/);
        const next = await s.api.transact(s.id, Uint8Array.of(0x80), 2, 100);
        assert.deepEqual(Array.from(next.data), [0xCC, 0xDD]);
        assert.equal(callbacks, 0);
    } finally { await s.close(); }
});
