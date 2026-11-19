// The mouse's back button in the desktop app's web views: hands it to the app's navigation instead of the web view's
// own history (which would change the page's route underneath the native navigation).
export function register(dotnet) {
    const isSideButton = (e) => e.button === 3 || e.button === 4;

    const swallow = (e) => {
        if (isSideButton(e)) {
            e.preventDefault();
            e.stopPropagation();
        }
    };

    const onUp = (e) => {
        if (!isSideButton(e)) {
            return;
        }

        swallow(e);
        if (e.button === 3) {
            dotnet.invokeMethodAsync("MouseBack");
        }
    };

    window.addEventListener("mousedown", swallow, true);
    window.addEventListener("mouseup", onUp, true);
    window.addEventListener("auxclick", swallow, true);

    return {
        dispose() {
            window.removeEventListener("mousedown", swallow, true);
            window.removeEventListener("mouseup", onUp, true);
            window.removeEventListener("auxclick", swallow, true);
        },
    };
}
