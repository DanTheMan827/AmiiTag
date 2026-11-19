import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../AmiiTag.Core/wwwroot/js/header-size.js', import.meta.url), 'utf8');
const { register } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture(initialHeight, observe = true) {
    const properties = new Map();
    const listeners = new Map();
    let height = initialHeight;
    let observer;
    const page = { style: {
        setProperty: (key, value) => properties.set(key, value),
        removeProperty: key => properties.delete(key)
    } };
    globalThis.window = {
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: name => listeners.delete(name)
    };
    globalThis.document = { fonts: { ready: Promise.resolve() } };
    globalThis.ResizeObserver = observe ? class {
        constructor(callback) { this.callback = callback; observer = this; }
        observe(element) { this.element = element; }
        disconnect() { this.disconnected = true; }
    } : undefined;
    const header = { closest: () => page, getBoundingClientRect: () => ({ height }) };
    return { header, properties, listeners, setHeight: value => height = value, observer: () => observer };
}

test('uses the actual initial navbar height and updates after actions or fonts resize it', async () => {
    const state = fixture(64);
    const binding = register(state.header);
    await Promise.resolve();
    assert.equal(state.properties.get('--web-header-height'), '64px');
    state.setHeight(92.5);
    state.observer().callback();
    assert.equal(state.properties.get('--web-header-height'), '92.5px');
    assert.deepEqual([...state.properties.keys()], ['--web-header-height']);
    binding.dispose();
});

test('disposal removes listeners, measurement, and late observer updates', async () => {
    const state = fixture(48);
    const binding = register(state.header);
    binding.dispose();
    state.setHeight(100);
    state.observer().callback();
    await Promise.resolve();
    assert.equal(state.properties.size, 0);
    assert.equal(state.listeners.size, 0);
    assert.equal(state.observer().disconnected, true);
});

test('window resize remains a fallback when ResizeObserver is unavailable', async () => {
    const state = fixture(44, false);
    const binding = register(state.header);
    await Promise.resolve();
    state.setHeight(70);
    state.listeners.get('resize')();
    assert.equal(state.properties.get('--web-header-height'), '70px');
    binding.dispose();
    assert.equal(state.listeners.size, 0);
});
