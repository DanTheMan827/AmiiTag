// Observe history traversal before Blazor's popstate handler. Blazor restores
// the current entry before asking .NET whether a route change may proceed.
// Keep the first destination through that restoration, without pushing entries
// or preventing normal links, Settings, or opening a tag from the device.
export function register() {
    let destination = null;
    const onPopState = () => { destination ??= window.location.href; };
    window.addEventListener('popstate', onPopState, true);
    return {
        takeTraversal(target) {
            const matches = destination === target;
            destination = null;
            return matches;
        },
        dispose() {
            window.removeEventListener('popstate', onPopState, true);
            destination = null;
        }
    };
}
