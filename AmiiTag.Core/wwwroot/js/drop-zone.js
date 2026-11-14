// Drag and drop of files onto parts of a page (DropZone.cs). Files are read here and handed to .NET as base64.
// Zones can nest: the innermost one under the pointer takes the drop. Anywhere else, dropped files are refused, so the
// browser (or the app's web view) never opens or navigates to them. Where a web view doesn't pass drops from other
// apps to the page (Android, iOS), the app catches them natively and forwards them through window.amiitagNativeDrop.

const MaxBytes = 32 * 1024 * 1024;
let guarded = false;

const hasFiles = (e) => !!e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files");

// While files are dragged over the window the root gets "dragging-files", so every zone that takes them can show it;
// the zone under the pointer gets "drop-active" on top of that. There's no reliable "left the window" event, so it
// also goes when the drag events stop coming.
let draggingTimer = 0;

const stopDragging = () => {
    clearTimeout(draggingTimer);
    draggingTimer = 0;
    document.documentElement.classList.remove("dragging-files");
};

const startDragging = () => {
    document.documentElement.classList.add("dragging-files");
    clearTimeout(draggingTimer);
    draggingTimer = setTimeout(stopDragging, 800);
};

// Registered zones: element -> { activate, clear, deliver }.
const zones = new WeakMap();
let nativeZone = null;

function zoneAt(x, y) {
    for (let element = document.elementFromPoint(x, y); element; element = element.parentElement) {
        const zone = zones.get(element);
        if (zone) {
            return zone;
        }
    }

    return null;
}

// Called by the app's native drag handling, with positions in CSS pixels from the web view's top left.
const nativeDrop = {
    over(x, y) {
        startDragging();
        const zone = zoneAt(x, y);
        if (nativeZone && nativeZone !== zone) {
            nativeZone.clear();
        }

        nativeZone = zone;
        zone?.activate();
        return !!zone;
    },
    leave() {
        stopDragging();
        nativeZone?.clear();
        nativeZone = null;
    },
    drop(x, y, files) {
        nativeDrop.leave();
        const zone = zoneAt(x, y);
        if (!zone || !files || files.length === 0) {
            return false;
        }

        zone.deliver(files);
        return true;
    },
};

export function installGuard() {
    if (guarded) {
        return;
    }

    guarded = true;
    window.amiitagNativeDrop = nativeDrop;

    // Capture phase, so zones that stop the event still keep the window's state up to date.
    const track = (e) => {
        if (hasFiles(e)) {
            startDragging();
        }
    };

    window.addEventListener("dragenter", track, true);
    window.addEventListener("dragover", track, true);
    window.addEventListener("dragleave", (e) => {
        // Leaving the window itself, not just moving between elements.
        if (hasFiles(e) && !e.relatedTarget &&
            (e.clientX <= 0 || e.clientY <= 0 || e.clientX >= window.innerWidth || e.clientY >= window.innerHeight)) {
            stopDragging();
        }
    }, true);
    window.addEventListener("drop", stopDragging, true);
    window.addEventListener("dragend", stopDragging, true);

    // Outside every zone, dropped files are refused rather than opened by the browser or web view.
    window.addEventListener("dragover", (e) => {
        if (hasFiles(e)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = "none";
        }
    });
    window.addEventListener("drop", (e) => {
        if (hasFiles(e)) {
            e.preventDefault();
        }
    });
}

function readFile(file) {
    return new Promise((resolve) => {
        if (file.size > MaxBytes) {
            resolve({ name: file.name, base64: null, problem: "too large" });
            return;
        }

        const reader = new FileReader();
        reader.onload = () => {
            const url = String(reader.result || "");
            const comma = url.indexOf(",");
            resolve({ name: file.name, base64: comma >= 0 ? url.substring(comma + 1) : "", problem: null });
        };

        // Folders (and anything else that can't be read) arrive like this.
        reader.onerror = () => resolve({ name: file.name, base64: null, problem: "not a file" });
        reader.readAsDataURL(file);
    });
}

export function register(element, dotnet) {
    installGuard();
    element.classList.add("drop-zone");

    // dragover repeats while the pointer is over the zone; the highlight goes when it stops (or the pointer leaves).
    let timer = 0;
    const clear = () => {
        clearTimeout(timer);
        timer = 0;
        element.classList.remove("drop-active");
    };

    const activate = () => {
        element.classList.add("drop-active");
        clearTimeout(timer);
        timer = setTimeout(clear, 800);
    };

    const over = (e) => {
        if (!hasFiles(e)) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();
        e.dataTransfer.dropEffect = "copy";
        activate();
    };

    const leave = (e) => {
        if (hasFiles(e) && !(e.relatedTarget && element.contains(e.relatedTarget))) {
            clear();
        }
    };

    const deliver = (files) => dotnet.invokeMethodAsync("OnFilesDropped", files);

    const drop = async (e) => {
        if (!hasFiles(e)) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();
        clear();

        // The list is only valid during the event.
        const files = Array.from(e.dataTransfer.files || []);
        if (files.length === 0) {
            return;
        }

        await deliver(await Promise.all(files.map(readFile)));
    };

    // A drop elsewhere, or a drag leaving the window, ends the highlight too.
    const end = () => clear();

    zones.set(element, { activate, clear, deliver });
    element.addEventListener("dragenter", over);
    element.addEventListener("dragover", over);
    element.addEventListener("dragleave", leave);
    element.addEventListener("drop", drop);
    window.addEventListener("drop", end, true);
    window.addEventListener("dragend", end, true);

    return {
        dispose() {
            clear();
            zones.delete(element);
            element.classList.remove("drop-zone");
            element.removeEventListener("dragenter", over);
            element.removeEventListener("dragover", over);
            element.removeEventListener("dragleave", leave);
            element.removeEventListener("drop", drop);
            window.removeEventListener("drop", end, true);
            window.removeEventListener("dragend", end, true);
        },
    };
}
