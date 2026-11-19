// Persistent web backing in IndexedDB, three stores:
//   storage    the app's own files (keys, dumps, lock-ons, settings): { key, value: Blob, lastAccessTime }.
//   cache      the amiibo database's files (amiibo.json, games_info.json, images): Blob values keyed by the SHA-1 the
//              database's sha1sum.json lists for them, so a file is downloaded once per version and never goes stale.
//   manifests  the last good sha1sum.json of each database source (Blob values keyed by its URL), for when it can't
//              be fetched.
//   parsed     the loaded database in a compact binary form (written by the app), one record: { key, value: Blob },
//              where key identifies the source files it was built from. Loading uses it instead of parsing the JSON
//              while those files are unchanged; it's replaced when any of them changes.
let connection;
let migration;
const objectUrls = new Map();
const pending = new Map();
let assetGeneration = 0;

function open() {
    if (!connection) connection = new Promise((resolve, reject) => {
        let abandoned = false;
        const request = indexedDB.open('AmiiTag', 4);
        request.onupgradeneeded = event => {
            const db = request.result;
            if (event.oldVersion < 1) {
                db.createObjectStore('storage', { keyPath: 'key' }).createIndex('lastAccessTime', 'lastAccessTime');
            }

            // Version 1 cached database files by URL in "assets"; they're downloaded again into the hash-keyed cache.
            if (db.objectStoreNames.contains('assets')) db.deleteObjectStore('assets');
            if (!db.objectStoreNames.contains('cache')) db.createObjectStore('cache');
            if (!db.objectStoreNames.contains('manifests')) db.createObjectStore('manifests');
            if (!db.objectStoreNames.contains('parsed')) db.createObjectStore('parsed');
            else if (event.oldVersion < 4) request.transaction.objectStore('parsed').clear(); // Earlier per-file copies.
        };
        request.onerror = () => reject(request.error);
        request.onblocked = () => { abandoned = true; reject(new Error('IndexedDB upgrade blocked by another window')); };
        request.onsuccess = () => {
            const db = request.result;
            if (abandoned) { db.close(); return; }
            db.onversionchange = () => { db.close(); connection = undefined; migration = undefined; };
            resolve(db);
        };
    }).catch(error => { connection = undefined; throw error; });
    return connection;
}

// Requests are queued synchronously, and read/touch happens inside onsuccess.
// Never await Blob conversion or fetch inside a live IndexedDB transaction.
function transact(db, name, mode, action) {
    return new Promise((resolve, reject) => {
        const transaction = db.transaction(name, mode);
        let result;
        transaction.oncomplete = () => resolve(result);
        transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted'));
        transaction.onerror = () => {}; // onabort reports the final transaction result.
        try { action(transaction.objectStore(name), value => { result = value; }); }
        catch (error) { transaction.abort(); reject(error); }
    });
}
function put(db, name, key, value) {
    return transact(db, name, 'readwrite', store => store.put({ key, value, lastAccessTime: Date.now() }));
}
function get(db, name, key) {
    return transact(db, name, 'readwrite', (store, result) => {
        const request = store.get(key);
        request.onsuccess = () => {
            const record = request.result;
            if (record) { record.lastAccessTime = Date.now(); store.put(record); }
            result(record?.value ?? null);
        };
    });
}

// The cache and manifest stores hold bare Blobs under out-of-line keys.
function putBlob(db, name, key, blob) {
    return transact(db, name, 'readwrite', store => store.put(blob, key));
}
function getBlob(db, name, key) {
    return transact(db, name, 'readonly', (store, result) => {
        const request = store.get(key);
        request.onsuccess = () => result(request.result ?? null);
    });
}
function allKeys(db, name) {
    return transact(db, name, 'readonly', (store, result) => {
        const request = store.getAllKeys();
        request.onsuccess = () => result(request.result);
    });
}

async function ready() {
    const db = await open();
    if (!migration) migration = (async () => {
        const keys = [];
        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            if (key?.startsWith('amiitag:')) keys.push(key);
        }
        for (const key of keys) {
            const name = key.slice(8);
            if (!await get(db, 'storage', name)) {
                const encoded = localStorage.getItem(key);
                if (encoded === null) continue;
                const value = Uint8Array.from(atob(encoded), char => char.charCodeAt(0));
                await put(db, 'storage', name, new Blob([value], { type: 'application/octet-stream' }));
            }
            localStorage.removeItem(key); // Only after the IndexedDB transaction committed.
        }
    })().catch(error => { migration = undefined; throw error; });
    await migration;
    return db;
}
export const storage = {
    async read(name) {
        const blob = await get(await ready(), 'storage', name);
        return blob === null ? null : new Uint8Array(await blob.arrayBuffer());
    },
    async write(name, bytes) {
        await put(await ready(), 'storage', name, new Blob([bytes], { type: 'application/octet-stream' }));
    },
    async remove(name) { await transact(await ready(), 'storage', 'readwrite', store => store.delete(name)); },
    async list(prefix) {
        return transact(await ready(), 'storage', 'readonly', (store, result) => {
            const request = store.getAllKeys();
            request.onsuccess = () => result(request.result.filter(key => key.startsWith(prefix)));
        });
    }
};

// A database source's sha1sum.json has one stable key whatever query was used to fetch it.
export function manifestKey(value) {
    const url = new URL(value, document.baseURI);
    url.hash = '';
    url.search = '';
    return url.href;
}

// The database URLs carry their file's hash as the query (images/icon_x.png?<sha1>); that hash is the cache key.
// Any hex digest is accepted; only a 40 digit one (SHA-1) is checked against the downloaded content.
export function hashOf(value) {
    const query = new URL(value, document.baseURI).search.slice(1).toLowerCase();
    return /^[0-9a-f]{8,128}$/.test(query) ? query : null;
}

function isManifest(url) { return new URL(url, document.baseURI).pathname.endsWith('/sha1sum.json'); }
function isJson(url) { return new URL(url, document.baseURI).pathname.endsWith('.json'); }

function validateJson(text, url) {
    const json = JSON.parse(text);
    if (!json || typeof json !== 'object' || Array.isArray(json)) throw new Error(`Invalid JSON: ${url}`);
    if (isManifest(url) && Object.values(json).some(value => typeof value !== 'string'))
        throw new Error(`Invalid hash manifest: ${url}`);
    if (new URL(url, document.baseURI).pathname.endsWith('/amiibo.json') &&
        (!json.amiibos || typeof json.amiibos !== 'object' || Array.isArray(json.amiibos)))
        throw new Error(`Invalid amiibo database: ${url}`);
}

async function sha1(blob) {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-1', await blob.arrayBuffer()));
    return Array.from(digest, byte => byte.toString(16).padStart(2, '0')).join('');
}

async function download(url) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 15000);
    try {
        const response = await fetch(url, { cache: 'no-cache', signal: controller.signal });
        if (!response.ok) throw new Error(`HTTP ${response.status}: ${url}`);
        const blob = await response.blob();
        if (!blob.size) throw new Error(`Empty file: ${url}`);
        if (isJson(url)) validateJson(await blob.text(), url);
        else if (!blob.type.startsWith('image/')) throw new Error(`Invalid image: ${url}`);
        return blob;
    } finally { clearTimeout(timeout); }
}

// A database file by its hash: from the cache, or downloaded and cached (unless cacheOnly). A download whose content
// doesn't match its hash is still used, but isn't cached under that hash.
async function cachedFile(url, cacheOnly) {
    const generation = assetGeneration;
    const db = await ready();
    const hash = hashOf(url);
    if (hash) {
        const cached = await getBlob(db, 'cache', hash);
        if (cached) return cached;
    }

    if (cacheOnly) throw new Error(`Not cached: ${url}`);
    const blob = await download(url);
    if (!hash) return blob;
    if (hash.length === 40 && await sha1(blob) !== hash) {
        console.warn(`AmiiTag: ${url} doesn't match its SHA-1; not caching it.`);
        return blob;
    }

    if (generation !== assetGeneration) throw new Error('Cache cleared during download');
    await putBlob(db, 'cache', hash, blob);
    return blob;
}
function sharedFile(url, cacheOnly) {
    const key = `${cacheOnly}:${hashOf(url) ?? url}`;
    if (!pending.has(key)) {
        const task = cachedFile(url, cacheOnly).finally(() => { if (pending.get(key) === task) pending.delete(key); });
        pending.set(key, task);
    }
    return pending.get(key);
}

export const assets = {
    // The source's sha1sum.json: always fetched fresh; the last committed one if that fails (or with cacheOnly).
    async readManifest(url, cacheOnly = false) {
        if (!cacheOnly) {
            try { return new Uint8Array(await (await download(url)).arrayBuffer()); }
            catch (error) { console.warn(`AmiiTag: couldn't fetch ${url}; using the cached copy.`, error); }
        }

        const cached = await getBlob(await ready(), 'manifests', manifestKey(url));
        if (!cached) throw new Error(`No cached hash manifest: ${manifestKey(url)}`);
        return new Uint8Array(await cached.arrayBuffer());
    },

    // Kept once every file of the source it lists has imported, so a failed update never strands the old data.
    async commitManifest(url, bytes) {
        if (!isManifest(url)) throw new Error('Expected sha1sum.json');
        const blob = new Blob([bytes], { type: 'application/json' });
        validateJson(await blob.text(), url);
        await putBlob(await ready(), 'manifests', manifestKey(url), blob);
    },

    // A database file (amiibo.json, games_info.json...) by the hash in its URL's query.
    async readFile(url, cacheOnly = false) {
        return new Uint8Array(await (await sharedFile(url, cacheOnly)).arrayBuffer());
    },

    // The saved parsed database if it was built from the source files `key` identifies, or null. One built from other
    // files (any of them has changed since) is invalid and is removed.
    async readSnapshot(key) {
        const db = await ready();
        const record = await getBlob(db, 'parsed', 'database');
        if (record?.key === key) return new Uint8Array(await record.value.arrayBuffer());
        if (record) await transact(db, 'parsed', 'readwrite', store => store.delete('database'));
        return null;
    },
    async writeSnapshot(key, bytes) {
        await putBlob(await ready(), 'parsed', 'database', { key, value: new Blob([bytes], { type: 'application/octet-stream' }) });
    },

    // The URLs whose files aren't in the cache yet (a URL without a hash is never cached).
    async uncached(urls) {
        const cached = new Set(await allKeys(await ready(), 'cache'));
        return urls.filter(url => { const hash = hashOf(url); return !hash || !cached.has(hash); });
    },

    // Downloads files into the cache, six at a time. Returns how many couldn't be (they show as placeholders).
    async cacheFiles(urls, cacheOnly = false) {
        const generation = assetGeneration;
        let next = 0, missing = 0;
        await Promise.all(Array.from({ length: Math.min(6, urls.length) }, async () => {
            while (next < urls.length && generation === assetGeneration) {
                const url = urls[next++];
                try { await sharedFile(url, cacheOnly); } catch { missing++; }
            }
        }));
        return missing;
    },

    // Removes every cached file whose hash isn't listed (the hashes of all sources' current manifests).
    async retain(hashes) {
        const keep = new Set(hashes.map(hash => hash.toLowerCase()));
        const db = await ready();
        const stale = (await allKeys(db, 'cache')).filter(key => !keep.has(key));
        if (stale.length) await transact(db, 'cache', 'readwrite', store => { for (const key of stale) store.delete(key); });
        for (const key of stale) {
            const objectUrl = objectUrls.get(key);
            if (objectUrl) { URL.revokeObjectURL(objectUrl); objectUrls.delete(key); }
        }
        return stale.length;
    },

    // A figure image as a document-scoped object URL of the cached Blob (downloaded and cached first if needed).
    async imageUrl(url) {
        const hash = hashOf(url);
        if (!hash) return null;
        if (!objectUrls.has(hash)) {
            let blob;
            try { blob = await sharedFile(url, false); } catch { return null; }
            if (!objectUrls.has(hash)) objectUrls.set(hash, URL.createObjectURL(blob));
        }
        return objectUrls.get(hash);
    },
    releaseImageUrls() {
        for (const url of objectUrls.values()) URL.revokeObjectURL(url);
        objectUrls.clear();
    },
    async clear() {
        assetGeneration++;
        pending.clear();
        const db = await ready();
        await transact(db, 'cache', 'readwrite', store => store.clear());
        await transact(db, 'manifests', 'readwrite', store => store.clear());
        await transact(db, 'parsed', 'readwrite', store => store.clear());
        assets.releaseImageUrls();
    }
};
