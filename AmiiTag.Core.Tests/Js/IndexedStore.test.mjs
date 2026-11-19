import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { IDBFactory, IDBObjectStore } from 'fake-indexeddb';
const source = await readFile(new URL('../../AmiiTag.WebAssembly/wwwroot/js/indexed-store.js', import.meta.url), 'utf8');
let unique = 0;
async function fixture() {
    globalThis.indexedDB = new IDBFactory();
    const legacy = new Map();
    globalThis.localStorage = { get length() { return legacy.size; }, key: i => [...legacy.keys()][i],
        getItem: key => legacy.get(key) ?? null, removeItem: key => legacy.delete(key) };
    globalThis.document = { baseURI: 'https://app/' };
    const calls = [];
    globalThis.fetch = async url => { calls.push(url); throw new Error('offline'); };
    const created = [], revoked = [];
    URL.createObjectURL = blob => { created.push(blob); return 'blob:test/' + created.length; };
    URL.revokeObjectURL = url => revoked.push(url);
    const module = await import(`data:text/javascript;base64,${Buffer.from(source + '\n// fixture '+(++unique)).toString('base64')}`);
    const db = () => new Promise((resolve, reject) => {
        const request = indexedDB.open('AmiiTag', 4); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
    });
    const record = async (store, key) => {
        const connection = await db();
        return new Promise((resolve, reject) => {
            const tx = connection.transaction(store); const request = tx.objectStore(store).get(key);
            tx.oncomplete = () => { connection.close(); resolve(request.result); }; tx.onabort = () => reject(tx.error);
        });
    };
    const keys = async store => {
        const connection = await db();
        return new Promise((resolve, reject) => {
            const tx = connection.transaction(store); const request = tx.objectStore(store).getAllKeys();
            tx.oncomplete = () => { connection.close(); resolve(request.result); }; tx.onabort = () => reject(tx.error);
        });
    };
    return { ...module, legacy, calls, created, revoked, record, keys };
}
const sha1 = text => createHash('sha1').update(text).digest('hex');
const jsonResponse = text => new Response(text, { headers: { 'Content-Type': 'application/json' } });
const imageResponse = text => new Response(text, { headers: { 'Content-Type': 'image/png' } });
const decode = bytes => new TextDecoder().decode(bytes);

test('storage persists binary Blobs, updates last access, and supports list/delete', async () => {
    const f = await fixture();
    await f.storage.write('keys/main', new Uint8Array([0, 255, 42]));
    const first = await f.record('storage', 'keys/main');
    assert.ok(first.value instanceof Blob); assert.equal(typeof first.lastAccessTime, 'number');
    const clock = Date.now; Date.now = () => first.lastAccessTime + 100;
    try { assert.deepEqual(await f.storage.read('keys/main'), new Uint8Array([0, 255, 42])); }
    finally { Date.now = clock; }
    assert.equal((await f.record('storage', 'keys/main')).lastAccessTime, first.lastAccessTime + 100);
    assert.deepEqual(await f.storage.list('keys/'), ['keys/main']);
    await f.storage.remove('keys/main'); assert.equal(await f.storage.read('keys/main'), null);
});

test('legacy localStorage data migrates without overwriting an existing IndexedDB record', async () => {
    const f = await fixture(); f.legacy.set('amiitag:settings', btoa('test')); f.legacy.set('unrelated', 'keep');
    assert.equal(decode(await f.storage.read('settings')), 'test');
    assert.equal(f.legacy.has('amiitag:settings'), false); assert.equal(f.legacy.get('unrelated'), 'keep');
    assert.ok((await f.record('storage', 'settings')).value instanceof Blob);
});

test('manifest keys drop the query; file hashes come from a hex digest query', async () => {
    const f = await fixture();
    assert.equal(f.manifestKey('https://db/sha1sum.json?random#x'), 'https://db/sha1sum.json');
    const hash = sha1('x');
    assert.equal(f.hashOf(`https://db/images/a.png?${hash.toUpperCase()}`), hash);
    assert.equal(f.hashOf('https://db/database/amiibo.json?'), null);
    assert.equal(f.hashOf('https://db/database/amiibo.json?v=1'), null);
});

test('the manifest is always fetched; its last committed copy is used when that fails', async () => {
    const f = await fixture();
    await f.assets.commitManifest('https://db/sha1sum.json?old', new TextEncoder().encode('{"database/amiibo.json":"old"}'));
    globalThis.fetch = async () => jsonResponse('{"database/amiibo.json":"new"}');
    assert.match(decode(await f.assets.readManifest('https://db/sha1sum.json?fresh')), /new/);
    // Reading it doesn't commit it: the old one stays until the import succeeds.
    assert.match(await (await f.record('manifests', 'https://db/sha1sum.json')).text(), /old/);
    globalThis.fetch = async () => { throw new Error('offline'); };
    assert.match(decode(await f.assets.readManifest('https://db/sha1sum.json?again')), /old/);
    globalThis.fetch = async () => new Response('<html>bad</html>');
    assert.match(decode(await f.assets.readManifest('https://db/sha1sum.json?again')), /old/);
});

test('database files are cached by hash, downloaded once, and read offline', async () => {
    const f = await fixture(); const body = '{"amiibos":{"test":{}}}'; const hash = sha1(body); let count = 0;
    globalThis.fetch = async () => { count++; return jsonResponse(body); };
    const url = `https://db/database/amiibo.json?${hash}`;
    assert.equal(decode(await f.assets.readFile(url)), body);
    assert.ok((await f.record('cache', hash)) instanceof Blob);
    globalThis.fetch = async () => { throw new Error('offline'); };
    assert.equal(decode(await f.assets.readFile(url)), body);
    assert.equal(decode(await f.assets.readFile(url, true)), body);
    assert.equal(count, 1);
    await assert.rejects(f.assets.readFile(`https://db/database/amiibo.json?${sha1('other')}`, true));
});

test('the parsed database snapshot is returned only for the key it was built from', async () => {
    const f = await fixture();
    assert.equal(await f.assets.readSnapshot('a'), null);
    await f.assets.writeSnapshot('a', new Uint8Array([1, 2, 3]));
    assert.deepEqual(await f.assets.readSnapshot('a'), new Uint8Array([1, 2, 3]));
    // Built from other files: invalid, and removed.
    assert.equal(await f.assets.readSnapshot('b'), null);
    assert.equal(await f.record('parsed', 'database'), undefined);
    await f.assets.writeSnapshot('b', new Uint8Array([4]));
    await f.assets.clear();
    assert.equal(await f.assets.readSnapshot('b'), null);
});

test('a download that does not match its hash is used but not cached; bad JSON is rejected', async () => {
    const f = await fixture(); const hash = sha1('expected');
    globalThis.fetch = async () => jsonResponse('{"amiibos":{}}');
    assert.equal(decode(await f.assets.readFile(`https://db/database/amiibo.json?${hash}`)), '{"amiibos":{}}');
    assert.deepEqual(await f.keys('cache'), []);
    globalThis.fetch = async () => new Response('<html>bad</html>');
    await assert.rejects(f.assets.readFile(`https://db/database/amiibo.json?${hash}`));
});

test('images: uncached lists what is missing, cacheFiles fills it, imageUrl shows the cached Blob offline', async () => {
    const f = await fixture(); let count = 0;
    globalThis.fetch = async () => { count++; return imageResponse('png'); };
    const url = `https://db/images/icon.png?${sha1('png')}`;
    assert.deepEqual(await f.assets.uncached([url]), [url]);
    assert.equal(await f.assets.cacheFiles([url]), 0);
    assert.deepEqual(await f.assets.uncached([url]), []);
    globalThis.fetch = async () => { throw new Error('offline'); };
    assert.equal(await f.assets.imageUrl(url), 'blob:test/1'); assert.equal(await f.assets.imageUrl(url), 'blob:test/1');
    assert.equal(f.created.length, 1); assert.equal(count, 1);
    assert.equal(await f.assets.imageUrl('https://db/images/nohash.png'), null);
});

test('retain removes every cached file not listed, and leaves app storage alone', async () => {
    const f = await fixture();
    globalThis.fetch = async url => imageResponse(new URL(url).pathname);
    const keep = `https://db/images/keep.png?${sha1('/images/keep.png')}`;
    const drop = `https://db/images/drop.png?${sha1('/images/drop.png')}`;
    await f.assets.cacheFiles([keep, drop]);
    await f.assets.imageUrl(drop);
    await f.storage.write('keys', new Uint8Array([7]));
    assert.equal(await f.assets.retain([sha1('/images/keep.png').toUpperCase()]), 1);
    assert.deepEqual(await f.keys('cache'), [sha1('/images/keep.png')]);
    assert.deepEqual(f.revoked, ['blob:test/1']);
    assert.deepEqual(await f.storage.read('keys'), new Uint8Array([7]));
});

test('clear empties the cache and manifests but not app storage', async () => {
    const f = await fixture();
    globalThis.fetch = async () => imageResponse('png');
    const url = `https://db/images/icon.png?${sha1('png')}`;
    await f.assets.imageUrl(url);
    await f.assets.commitManifest('https://db/sha1sum.json', new TextEncoder().encode('{}'));
    await f.storage.write('keys', new Uint8Array([7]));
    await f.assets.clear();
    assert.deepEqual(f.revoked, ['blob:test/1']);
    assert.deepEqual(await f.keys('cache'), []); assert.deepEqual(await f.keys('manifests'), []);
    assert.deepEqual(await f.storage.read('keys'), new Uint8Array([7]));
});

test('file prefetch has bounded concurrency and missing files do not fail', async () => {
    const f = await fixture(); let active = 0, peak = 0;
    globalThis.fetch = async url => {
        active++; peak = Math.max(peak, active); await new Promise(resolve => setTimeout(resolve, 2)); active--;
        return url.includes('missing') ? new Response('', { status: 404 }) : imageResponse(new URL(url).pathname);
    };
    const urls = Array.from({ length: 12 }, (_, i) => `https://db/${i}.png?${sha1(`/${i}.png`)}`);
    assert.equal(await f.assets.cacheFiles([...urls, `https://db/missing.png?${sha1('x')}`]), 1);
    assert.ok(peak <= 6); assert.ok(peak > 1);
});

test('clearing during a download prevents old work from repopulating the cache', async () => {
    const f = await fixture(); let finish;
    globalThis.fetch = () => new Promise(resolve => { finish = resolve; });
    const url = `https://db/old.png?${sha1('old')}`;
    const pending = f.assets.readFile(url);
    while (!finish) await new Promise(resolve => setTimeout(resolve, 0));
    await f.assets.clear(); finish(imageResponse('old'));
    await assert.rejects(pending, /cleared/);
    assert.deepEqual(await f.keys('cache'), []);
});

test('cache-only prefetch never starts a network request', async () => {
    const f = await fixture(); let requests = 0;
    globalThis.fetch = async () => { requests++; throw new Error('must stay offline'); };
    assert.equal(await f.assets.cacheFiles([`https://db/missing.png?${sha1('x')}`], true), 1);
    assert.equal(requests, 0);
});

test('upgrading from version 1 drops the URL-keyed asset store and keeps app storage', async () => {
    globalThis.indexedDB = new IDBFactory();
    await new Promise((resolve, reject) => {
        const request = indexedDB.open('AmiiTag', 1);
        request.onupgradeneeded = () => {
            for (const name of ['storage', 'assets']) request.result.createObjectStore(name, { keyPath: 'key' }).createIndex('lastAccessTime', 'lastAccessTime');
        };
        request.onsuccess = () => {
            const db = request.result; const tx = db.transaction('storage', 'readwrite');
            tx.objectStore('storage').put({ key: 'keys', value: new Blob([new Uint8Array([9])]), lastAccessTime: 1 });
            tx.oncomplete = () => { db.close(); resolve(); }; tx.onabort = () => reject(tx.error);
        };
        request.onerror = () => reject(request.error);
    });
    const legacy = new Map();
    globalThis.localStorage = { get length() { return legacy.size; }, key: i => [...legacy.keys()][i], getItem: () => null, removeItem: () => {} };
    globalThis.document = { baseURI: 'https://app/' };
    const module = await import(`data:text/javascript;base64,${Buffer.from(source + '\n// upgrade '+(++unique)).toString('base64')}`);
    assert.deepEqual(await module.storage.read('keys'), new Uint8Array([9]));
    const names = await new Promise(resolve => {
        const request = indexedDB.open('AmiiTag', 4);
        request.onsuccess = () => { const list = [...request.result.objectStoreNames]; request.result.close(); resolve(list); };
    });
    assert.deepEqual(names.sort(), ['cache', 'manifests', 'parsed', 'storage']);
});

test('failed migration keeps legacy bytes and can retry after a transaction error', async () => {
    const f = await fixture(); f.legacy.set('amiitag:keys', btoa('private-key-bytes'));
    const original = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function(record) {
        if (this.name === 'storage' && record.key === 'keys') throw new DOMException('quota', 'QuotaExceededError');
        return original.call(this, record);
    };
    try { await assert.rejects(f.storage.read('keys'), /quota/); }
    finally { IDBObjectStore.prototype.put = original; }
    assert.equal(f.legacy.get('amiitag:keys'), btoa('private-key-bytes'));
    assert.equal(decode(await f.storage.read('keys')), 'private-key-bytes');
    assert.equal(f.legacy.has('amiitag:keys'), false);
});
