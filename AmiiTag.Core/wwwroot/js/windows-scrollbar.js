// Windows-only overlay scrollbar for the document. The document continues to
// scroll behind native chrome; only this control starts at --native-top.
const registrations = new WeakMap();
const activeClass = 'windows-custom-scrollbar-active';
const clamp = (value, max) => Math.max(0, Math.min(value, max));

export function register(track, thumb, content) {
    const root = document.documentElement;
    const scroller = document.scrollingElement;
    if (!root || !scroller || !track || !thumb || !content) throw new Error('Scrollbar elements unavailable');
    registrations.get(root)?.dispose();
    let disposed = false;
    let frame = 0;
    let drag = null;
    let metrics = { max: 0, travel: 0, height: 0, viewport: 0 };
    const removers = [];
    const listen = (target, name, handler, options) => {
        target.addEventListener(name, handler, options);
        removers.push(() => target.removeEventListener(name, handler, options));
    };
    const attribute = (name, value) => {
        value = String(value);
        if (track.getAttribute(name) !== value) track.setAttribute(name, value);
    };
    const update = () => {
        if (disposed) return;
        const viewport = scroller.clientHeight;
        const max = Math.max(0, scroller.scrollHeight - viewport);
        track.hidden = max <= 1;
        const height = track.hidden ? 0 : track.getBoundingClientRect().height;
        const thumbHeight = Math.min(height, Math.max(28, height * viewport / Math.max(1, scroller.scrollHeight)));
        const travel = Math.max(0, height - thumbHeight);
        const current = clamp(scroller.scrollTop, max);
        metrics = { max, travel, height: thumbHeight, viewport };
        track.hidden = max <= 1 || height <= 0;
        track.tabIndex = track.hidden ? -1 : 0;
        thumb.style.height = `${thumbHeight}px`;
        thumb.style.transform = `translateY(${max > 0 ? current / max * travel : 0}px)`;
        attribute('aria-valuemax', Math.round(max));
        attribute('aria-valuenow', Math.round(current));
        attribute('aria-valuetext', `${max > 0 ? Math.round(current / max * 100) : 0}%`);
    };
    const schedule = () => {
        if (!disposed && !frame) frame = requestAnimationFrame(() => { frame = 0; update(); });
    };
    const scrollTo = value => {
        scroller.scrollTop = clamp(value, metrics.max);
        update();
    };
    const movePointer = event => {
        if (!drag || drag.id !== event.pointerId || metrics.travel <= 0) return;
        const top = event.clientY - track.getBoundingClientRect().top - Math.min(drag.grab, metrics.height);
        scrollTo(top / metrics.travel * metrics.max);
    };
    const endDrag = event => {
        if (!drag || event && event.pointerId !== undefined && event.pointerId !== drag.id) return;
        const id = drag.id;
        drag = null;
        track.classList.remove('dragging');
        if (track.hasPointerCapture(id)) track.releasePointerCapture(id);
    };
    const pointerDown = event => {
        if (event.button !== 0 || track.hidden || drag) return;
        event.preventDefault();
        track.classList.add('pointer-focus');
        track.focus({ preventScroll: true });
        update();
        drag = {
            id: event.pointerId,
            grab: thumb.contains(event.target) ? event.clientY - thumb.getBoundingClientRect().top : metrics.height / 2
        };
        track.setPointerCapture(event.pointerId);
        track.classList.add('dragging');
        if (!thumb.contains(event.target)) movePointer(event);
    };
    const keyboardFocus = () => track.classList.remove('pointer-focus');
    const keyDown = event => {
        keyboardFocus();
        if (event.altKey || event.ctrlKey || event.metaKey || track.hidden) return;
        const page = metrics.viewport * 0.9;
        const current = scroller.scrollTop;
        let target;
        switch (event.key) {
            case 'ArrowUp': target = current - 40; break;
            case 'ArrowDown': target = current + 40; break;
            case 'PageUp': target = current - page; break;
            case 'PageDown': target = current + page; break;
            case 'Home': target = 0; break;
            case 'End': target = metrics.max; break;
            case ' ': target = current + (event.shiftKey ? -page : page); break;
            default: return;
        }
        event.preventDefault();
        scrollTo(target);
    };
    const wheel = event => {
        if (track.hidden || !event.deltaY) return;
        event.preventDefault();
        const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? metrics.viewport : 1;
        scrollTo(scroller.scrollTop + event.deltaY * unit);
    };
    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(schedule) : null;
    const contentChanges = typeof MutationObserver === 'function' ? new MutationObserver(schedule) : null;
    const insetChanges = typeof MutationObserver === 'function' ? new MutationObserver(schedule) : null;
    const binding = { dispose() {
        if (disposed) return;
        disposed = true;
        endDrag();
        keyboardFocus();
        if (frame) cancelAnimationFrame(frame);
        resize?.disconnect();
        contentChanges?.disconnect();
        insetChanges?.disconnect();
        for (const remove of removers) remove();
        track.hidden = true;
        track.tabIndex = -1;
        if (registrations.get(root) === binding) {
            registrations.delete(root);
            root.classList.remove(activeClass);
        }
    } };
    try {
        listen(window, 'scroll', schedule, { passive: true });
        listen(window, 'resize', schedule);
        listen(window, 'blur', () => endDrag());
        listen(window, 'keydown', keyboardFocus, true);
        listen(track, 'blur', keyboardFocus);
        listen(content, 'load', schedule, true);
        listen(track, 'pointerdown', pointerDown);
        listen(track, 'pointermove', movePointer);
        listen(track, 'pointerup', endDrag);
        listen(track, 'pointercancel', endDrag);
        listen(track, 'lostpointercapture', endDrag);
        listen(track, 'keydown', keyDown);
        listen(track, 'wheel', wheel, { passive: false });
        resize?.observe(content);
        resize?.observe(root);
        resize?.observe(track);
        contentChanges?.observe(content, { childList: true, subtree: true, attributes: true, characterData: true });
        insetChanges?.observe(root, { attributes: true, attributeFilter: ['style'] });
        document.fonts?.ready.then(schedule);
        registrations.set(root, binding);
        root.classList.add(activeClass);
        update();
        return binding;
    } catch (error) {
        binding.dispose(); // Restore the native scrollbar if setup cannot finish.
        throw error;
    }
}
