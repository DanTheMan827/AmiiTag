import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../AmiiTag.Core/wwwroot/js/windows-scrollbar.js', import.meta.url), 'utf8');
const { register } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

class Element {
    constructor() {
        this.listeners = new Map(); this.attributes = new Map(); this.classes = new Set(); this.style = {};
        this.hidden = true; this.tabIndex = -1; this.captured = new Set();
        this.classList = { add: x => this.classes.add(x), remove: x => this.classes.delete(x) };
    }
    addEventListener(type, fn) { if (!this.listeners.has(type)) this.listeners.set(type, new Set()); this.listeners.get(type).add(fn); }
    removeEventListener(type, fn) { this.listeners.get(type)?.delete(fn); }
    fire(type, values = {}) {
        const event = { target: this, button: 0, pointerId: 1, preventDefault() { this.prevented = true; }, ...values };
        for (const fn of this.listeners.get(type) ?? []) fn(event);
        return event;
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    focus(options) { this.focusOptions = options; }
    contains(target) { return target === this; }
    setPointerCapture(id) { this.captured.add(id); }
    hasPointerCapture(id) { return this.captured.has(id); }
    releasePointerCapture(id) { this.captured.delete(id); }
}
function fixture() {
    const root = new Element(), track = new Element(), thumb = new Element(), content = new Element(), window = new Element();
    const scroller = { clientHeight: 600, scrollHeight: 1800, scrollTop: 0 };
    let top = 80;
    const frames = new Map(); let id = 0;
    const observers = [];
    class Observer {
        constructor(fn) { this.fn = fn; this.targets = []; observers.push(this); }
        observe(target) { this.targets.push(target); }
        disconnect() { this.disconnected = true; }
    }
    track.getBoundingClientRect = () => ({ top, height: track.hidden ? 0 : Math.max(0, scroller.clientHeight - top) });
    thumb.getBoundingClientRect = () => ({ top: top + Number.parseFloat(thumb.style.transform?.slice(11) ?? 0) });
    globalThis.window = window;
    globalThis.document = { documentElement: root, scrollingElement: scroller, fonts: { ready: Promise.resolve() } };
    globalThis.ResizeObserver = Observer;
    globalThis.MutationObserver = Observer;
    globalThis.requestAnimationFrame = fn => { frames.set(++id, fn); return id; };
    globalThis.cancelAnimationFrame = key => frames.delete(key);
    const f = { root, track, thumb, content, window, scroller, observers, frames,
        setTop: value => { top = value; },
        flush: () => { const pending = [...frames.values()]; frames.clear(); for (const fn of pending) fn(); },
        resize: () => { observers[0].fn(); f.flush(); }
    };
    f.binding = register(track, thumb, content);
    return f;
}
const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 0.01, `${actual} != ${expected}`);
const position = f => Number.parseFloat(f.thumb.style.transform.slice(11));

test('thumb tracks document scroll while its track excludes the navbar', () => {
    const f = fixture();
    assert.ok(f.root.classes.has('windows-custom-scrollbar-active'));
    close(Number.parseFloat(f.thumb.style.height), 520 / 3);
    f.scroller.scrollTop = 600; f.window.fire('scroll'); f.flush();
    close(position(f), (520 - 520 / 3) / 2);
    assert.equal(f.track.getAttribute('aria-valuemax'), '1200');
    assert.equal(f.track.getAttribute('aria-valuenow'), '600');
    assert.equal(f.track.getAttribute('aria-valuetext'), '50%');
    assert.equal(f.track.tabIndex, 0);
    f.binding.dispose();
});

test('thumb drag preserves the grab point and ends cleanly on pointer cancellation', () => {
    const f = fixture(); const travel = 520 - 520 / 3;
    const down = f.track.fire('pointerdown', { target: f.thumb, clientY: 90 });
    assert.equal(down.prevented, true); assert.deepEqual(f.track.focusOptions, { preventScroll: true });
    assert.equal(f.track.hasPointerCapture(1), true);
    f.track.fire('pointermove', { clientY: 90 + travel / 2 });
    close(f.scroller.scrollTop, 600);
    f.track.fire('pointermove', { pointerId: 2, clientY: 1000 });
    close(f.scroller.scrollTop, 600);
    f.track.fire('pointercancel');
    assert.equal(f.track.hasPointerCapture(1), false); assert.equal(f.track.classes.has('dragging'), false);
    f.track.fire('pointermove', { clientY: 1000 }); close(f.scroller.scrollTop, 600);
    f.binding.dispose();
});

test('track clicks and keyboard keys clamp to the document scroll range', () => {
    const f = fixture();
    f.track.fire('pointerdown', { clientY: 600 }); close(f.scroller.scrollTop, 1200);
    f.track.fire('pointerup');
    f.track.fire('keydown', { key: 'Home' }); close(f.scroller.scrollTop, 0);
    f.track.fire('keydown', { key: 'PageDown' }); close(f.scroller.scrollTop, 540);
    f.track.fire('keydown', { key: 'ArrowDown' }); close(f.scroller.scrollTop, 580);
    f.track.fire('keydown', { key: 'ArrowUp' }); close(f.scroller.scrollTop, 540);
    f.track.fire('keydown', { key: ' ', shiftKey: true }); close(f.scroller.scrollTop, 0);
    f.track.fire('keydown', { key: 'End' }); close(f.scroller.scrollTop, 1200);
    f.track.fire('keydown', { key: 'PageUp' }); close(f.scroller.scrollTop, 660);
    assert.equal(f.track.fire('keydown', { key: 'Home', ctrlKey: true }).prevented, undefined);
    close(f.scroller.scrollTop, 660);
    f.binding.dispose();
});

test('wheel on the custom track scrolls the document for pixel, line and page units', () => {
    const f = fixture();
    assert.equal(f.track.fire('wheel', { deltaY: 20, deltaMode: 0 }).prevented, true);
    close(f.scroller.scrollTop, 20);
    f.track.fire('wheel', { deltaY: 2, deltaMode: 1 }); close(f.scroller.scrollTop, 52);
    f.track.fire('wheel', { deltaY: 1, deltaMode: 2 }); close(f.scroller.scrollTop, 652);
    f.binding.dispose();
});

test('navbar/content resizing updates metrics without resetting the current scroll', () => {
    const f = fixture(); f.scroller.scrollTop = 600;
    f.setTop(120); f.observers[2].fn(); f.flush();
    close(Number.parseFloat(f.thumb.style.height), 160); close(position(f), 160); close(f.scroller.scrollTop, 600);
    f.scroller.scrollHeight = 2400; f.content.fire('load'); f.flush();
    assert.equal(f.track.getAttribute('aria-valuemax'), '1800');
    close(Number.parseFloat(f.thumb.style.height), 120); close(position(f), 120);
    f.binding.dispose();
});

test('no-overflow pages hide the control and content growth restores it', () => {
    const f = fixture(); f.scroller.scrollHeight = 600; f.resize();
    assert.equal(f.track.hidden, true); assert.equal(f.track.tabIndex, -1);
    assert.equal(f.track.getAttribute('aria-valuemax'), '0');
    f.scroller.scrollHeight = 1800; f.resize();
    assert.equal(f.track.hidden, false); assert.equal(f.track.tabIndex, 0);
    f.binding.dispose();
});

test('disposal releases capture/listeners and restores the native scrollbar; replacement ownership is safe', async () => {
    const f = fixture(); f.track.fire('pointerdown', { clientY: 90, target: f.thumb });
    f.window.fire('scroll');
    const replacement = register(f.track, f.thumb, f.content);
    f.binding.dispose();
    assert.ok(f.root.classes.has('windows-custom-scrollbar-active'));
    replacement.dispose(); await Promise.resolve();
    assert.equal(f.root.classes.has('windows-custom-scrollbar-active'), false);
    assert.equal(f.track.hidden, true); assert.equal(f.track.tabIndex, -1); assert.equal(f.track.captured.size, 0);
    assert.equal(f.frames.size, 0);
    for (const element of [f.window, f.track, f.content])
        for (const listeners of element.listeners.values()) assert.equal(listeners.size, 0);
    assert.ok(f.observers.every(o => o.disconnected));
});

test('registration failure leaves the document scrollbar available and removes partial listeners', () => {
    const f = fixture(); f.binding.dispose(); f.scroller.scrollTop = 300;
    f.track.getBoundingClientRect = () => { throw new Error('layout unavailable'); };
    assert.throws(() => register(f.track, f.thumb, f.content), /layout unavailable/);
    assert.equal(f.root.classes.has('windows-custom-scrollbar-active'), false);
    assert.equal(f.track.hidden, true);
    close(f.scroller.scrollTop, 300);
    for (const element of [f.window, f.track, f.content])
        for (const listeners of element.listeners.values()) assert.equal(listeners.size, 0);
});

test('pointer focus ring suppression clears for keyboard navigation and blur', () => {
    const f = fixture();
    f.track.fire('pointerdown', { target: f.thumb, clientY: 90 });
    assert.equal(f.track.classes.has('pointer-focus'), true);
    f.track.fire('pointerup');
    assert.equal(f.track.classes.has('pointer-focus'), true);
    f.window.fire('keydown', { key: 'Tab' });
    assert.equal(f.track.classes.has('pointer-focus'), false);
    f.track.fire('pointerdown', { target: f.thumb, clientY: 90 });
    f.track.fire('pointerup');
    f.track.fire('keydown', { key: 'ArrowDown' });
    assert.equal(f.track.classes.has('pointer-focus'), false);
    close(f.scroller.scrollTop, 40);
    f.track.fire('pointerdown', { target: f.thumb, clientY: 90 });
    f.track.fire('pointerup');
    f.track.fire('blur');
    assert.equal(f.track.classes.has('pointer-focus'), false);
    f.binding.dispose();
});

test('track end spacing is included in measured thumb geometry and bottom alignment', () => {
    const f = fixture();
    f.track.getBoundingClientRect = () => ({ top: 83, height: 514 }); // 80px navbar + 3px top/bottom gaps.
    f.resize();
    f.track.fire('keydown', { key: 'End' });
    close(position(f) + Number.parseFloat(f.thumb.style.height), 514);
    close(83 + position(f) + Number.parseFloat(f.thumb.style.height), 597);
    close(f.scroller.scrollTop, 1200);
    f.binding.dispose();
});
