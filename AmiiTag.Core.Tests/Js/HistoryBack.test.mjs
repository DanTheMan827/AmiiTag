import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../AmiiTag.Core/wwwroot/js/history-back.js', import.meta.url), 'utf8');
const { register } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture() {
    const listeners = new Set();
    globalThis.window = {
        location: { href: 'https://app/device/pixl' },
        addEventListener: (type, fn, capture) => { assert.equal(type, 'popstate'); assert.equal(capture, true); listeners.add(fn); },
        removeEventListener: (type, fn, capture) => { assert.equal(capture, true); listeners.delete(fn); }
    };
    return { listeners, pop(url) { window.location.href = url; for (const fn of listeners) fn(); } };
}

test('browser Back survives Blazor restoring the device URL before .NET runs', () => {
    const f = fixture(); const guard = register();
    f.pop('https://app/');
    f.pop('https://app/device/pixl');
    assert.equal(guard.takeTraversal('https://app/'), true);
    assert.equal(guard.takeTraversal('https://app/'), false);
    guard.dispose();
});

test('links and programmatic Settings/tag navigation are not history traversal', () => {
    const f = fixture(); const guard = register();
    assert.equal(guard.takeTraversal('https://app/settings'), false);
    f.pop('https://app/');
    assert.equal(guard.takeTraversal('https://app/settings'), false);
    assert.equal(guard.takeTraversal('https://app/'), false);
    guard.dispose();
});

test('repeated Back requests each produce a new folder traversal', () => {
    const f = fixture(); const guard = register();
    for (let i = 0; i < 3; i++) {
        f.pop('https://app/'); f.pop('https://app/device/pixl');
        assert.equal(guard.takeTraversal('https://app/'), true);
    }
    guard.dispose();
});

test('disposal removes the listener and forgets stale navigation', () => {
    const f = fixture(); const guard = register();
    f.pop('https://app/'); guard.dispose();
    assert.equal(f.listeners.size, 0);
    assert.equal(guard.takeTraversal('https://app/'), false);
});
