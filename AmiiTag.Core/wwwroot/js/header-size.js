// Sticky page content must clear the actual web navbar, including safe-area
// padding, font changes, and resized header actions.
export function register(header) {
    const page = header?.closest('.app-page');
    let disposed = false;
    const measure = () => {
        if (disposed || !page) return;
        const height = Math.max(0, header.getBoundingClientRect().height);
        page.style.setProperty('--web-header-height', `${height}px`);
    };
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(measure) : null;
    if (header) observer?.observe(header);
    window.addEventListener('resize', measure);
    document.fonts?.ready.then(measure);
    measure();
    return {
        dispose() {
            disposed = true;
            observer?.disconnect();
            window.removeEventListener('resize', measure);
            page?.style.removeProperty('--web-header-height');
        }
    };
}
