// The native WebView owns the pull gesture. This only prevents a parent refresh
// from stealing a gesture in a nested HTML scroller, picker, or context menu.
// No navigation, location change, or document reload occurs here.
export function register(dotnet) {
    let lastScroller = null;
    let lastBlocked = null;
    let disposed = false;
    let scheduled = 0;
    const check = () => {
        scheduled = 0;
        if (disposed) return;
        const overlay = [...document.querySelectorAll('.dialog-backdrop, .context-menu-backdrop')]
            .some(element => element.getClientRects().length > 0);
        const nested = lastScroller?.isConnected && lastScroller.scrollTop > 0;
        const blocked = Boolean(overlay || nested);
        if (blocked !== lastBlocked) {
            lastBlocked = blocked;
            dotnet.invokeMethodAsync('SetPullBlocked', blocked).catch(() => {});
        }
    };
    const schedule = () => { if (!disposed && !scheduled) scheduled = requestAnimationFrame(check); };
    const scrolled = event => {
        const target = event.target;
        if (target instanceof Element && target !== document.scrollingElement && target !== document.body)
            lastScroller = target;
        schedule();
    };
    const observer = new MutationObserver(schedule);
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
    document.addEventListener('scroll', scrolled, true);
    check();
    return {
        dispose() {
            disposed = true;
            if (scheduled) cancelAnimationFrame(scheduled);
            observer.disconnect();
            document.removeEventListener('scroll', scrolled, true);
        }
    };
}
