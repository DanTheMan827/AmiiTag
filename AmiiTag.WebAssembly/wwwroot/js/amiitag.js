// Browser helpers for the AmiiTag WebAssembly host.
// Exposed on window.amiitag so .NET can call them with IJSRuntime ("amiitag.bluetooth.connect", ...).
(function () {
    "use strict";

    const amiitag = (window.amiitag = window.amiitag || {});

    // ------------------------------------------------------------------ Navigation

    amiitag.goBack = function () {
        if (window.history.length > 1) {
            window.history.back();
            return true;
        }

        return false;
    };

    // True when running as an installed app (home screen PWA) on iPhone/iPad. That's the only place with no back
    // control at all: browsers have a back button and Android has the system back gesture.
    amiitag.isIosStandalone = function () {
        const standalone =
            window.navigator.standalone === true ||
            window.matchMedia("(display-mode: standalone)").matches ||
            window.matchMedia("(display-mode: fullscreen)").matches;
        const ios =
            /iPhone|iPad|iPod/i.test(navigator.userAgent) ||
            (/Macintosh/i.test(navigator.userAgent) && navigator.maxTouchPoints > 1); // iPadOS reports as a Mac
        return standalone && ios;
    };

    // ------------------------------------------------------------------ Files

    function bytesToBase64(bytes) {
        let binary = "";
        for (let i = 0; i < bytes.length; i += 0x8000) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        }

        return btoa(binary);
    }

    // Debug builds: saves the in-memory log of this page load (see DebugLog.cs). Run from the browser console.
    amiitag.downloadDebugLog = async function () {
        const text = await DotNet.invokeMethodAsync("AmiiTag.WebAssembly", "GetDebugLog");
        amiitag.files.download("AmiiTag.log", new TextEncoder().encode(text), "text/plain");
    };

    amiitag.files = {
        // Resolves to { name, base64 } or null if cancelled. Call from a user gesture.
        pick: function (accept) {
            return new Promise(function (resolve) {
                const input = document.createElement("input");
                input.type = "file";
                if (accept) {
                    input.accept = accept;
                }

                input.style.display = "none";
                document.body.appendChild(input);

                let settled = false;
                const finish = function (value) {
                    if (!settled) {
                        settled = true;
                        input.remove();
                        resolve(value);
                    }
                };

                input.addEventListener("change", async function () {
                    const file = input.files && input.files[0];
                    if (!file) {
                        finish(null);
                        return;
                    }

                    const bytes = new Uint8Array(await file.arrayBuffer());
                    finish({ name: file.name, base64: bytesToBase64(bytes) });
                });
                input.addEventListener("cancel", function () {
                    finish(null);
                });
                input.click();
            });
        },

        // Resolves to an array of { name, base64 } (empty if cancelled). Call from a user gesture.
        pickMany: function (accept) {
            return new Promise(function (resolve) {
                const input = document.createElement("input");
                input.type = "file";
                input.multiple = true;
                if (accept) {
                    input.accept = accept;
                }

                input.style.display = "none";
                document.body.appendChild(input);

                let settled = false;
                const finish = function (value) {
                    if (!settled) {
                        settled = true;
                        input.remove();
                        resolve(value);
                    }
                };

                input.addEventListener("change", async function () {
                    const picked = [];
                    for (const file of Array.from(input.files || [])) {
                        const bytes = new Uint8Array(await file.arrayBuffer());
                        picked.push({ name: file.name, base64: bytesToBase64(bytes) });
                    }

                    finish(picked);
                });
                input.addEventListener("cancel", function () {
                    finish([]);
                });
                input.click();
            });
        },

        download: function (name, data, contentType) {
            const blob = new Blob([data], { type: contentType || "application/octet-stream" });
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = name;
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(function () {
                URL.revokeObjectURL(url);
            }, 30000);
        },
    };

    // ------------------------------------------------------------------ Storage (localStorage, base64 values)

    const storagePrefix = "amiitag:";

    amiitag.storage = {
        read: function (name) {
            return localStorage.getItem(storagePrefix + name);
        },
        write: function (name, base64) {
            localStorage.setItem(storagePrefix + name, base64);
        },
        remove: function (name) {
            localStorage.removeItem(storagePrefix + name);
        },
        list: function (prefix) {
            const names = [];
            for (let i = 0; i < localStorage.length; i++) {
                const key = localStorage.key(i);
                if (key && key.startsWith(storagePrefix + prefix)) {
                    names.push(key.substring(storagePrefix.length));
                }
            }

            return names;
        },
    };

    // ------------------------------------------------------------------ WebBluetooth

    const bleDevices = new Map();
    const bleCharacteristics = new Map();

    // Web Bluetooth allows one GATT operation per device at a time: another started before the last settles fails with
    // "GATT operation already in progress". Every operation on a device goes through its queue. (A .NET call that was
    // cancelled doesn't stop its JS operation, so the queue also keeps those from overlapping the next one.)
    const bleQueues = new Map();

    function enqueue(id, operation) {
        const previous = bleQueues.get(id) || Promise.resolve();
        const next = previous.catch(function () { }).then(operation);
        bleQueues.set(id, next.catch(function () { }));
        return next;
    }

    function delay(ms) {
        return new Promise(function (resolve) { setTimeout(resolve, ms); });
    }

    function isTransient(e) {
        const message = (e && e.message) || "";
        return (e && e.name === "NetworkError") || /in progress|unknown reason|GATT operation failed/i.test(message);
    }

    // Service/characteristic lookups and notification setup are safe to repeat; Chrome often fails them right after
    // connecting. Writes are never retried, as the device may already have acted on them.
    async function withRetries(operation) {
        for (let attempt = 0; ; attempt++) {
            try {
                return await operation();
            } catch (e) {
                if (attempt >= 2 || !isTransient(e)) {
                    throw e;
                }

                await delay(100 * (attempt + 1));
            }
        }
    }

    function charKey(id, service, characteristic) {
        return id + "|" + service + "|" + characteristic;
    }

    function requireDevice(id) {
        const device = bleDevices.get(id);
        if (!device) {
            throw new Error("Bluetooth device not found. Pair it again.");
        }

        return device;
    }

    function requireCharacteristic(id, service, characteristic) {
        const value = bleCharacteristics.get(charKey(id, service, characteristic));
        if (!value) {
            throw new Error("Characteristic " + characteristic + " isn't available.");
        }

        return value;
    }

    function describeDevice(device) {
        return { id: device.id, name: device.name || null };
    }

    amiitag.bluetooth = {
        // The API exists (Chrome/Edge). isAvailable additionally checks for an adapter.
        isSupported: function () {
            return !!navigator.bluetooth;
        },

        isAvailable: async function () {
            if (!navigator.bluetooth) {
                return false;
            }

            try {
                return navigator.bluetooth.getAvailability ? await navigator.bluetooth.getAvailability() : true;
            } catch {
                return true;
            }
        },

        // filter: { services: [], names: [], namePrefixes: [], optionalServices: [] }
        requestDevice: async function (filter) {
            const filters = [];
            (filter.services || []).forEach(function (s) {
                filters.push({ services: [s] });
            });
            (filter.names || []).forEach(function (n) {
                filters.push({ name: n });
            });
            (filter.namePrefixes || []).forEach(function (p) {
                filters.push({ namePrefix: p });
            });

            try {
                const device = await navigator.bluetooth.requestDevice({
                    filters: filters,
                    optionalServices: filter.optionalServices || [],
                });
                bleDevices.set(device.id, device);
                return describeDevice(device);
            } catch (e) {
                if (e && e.name === "NotFoundError") {
                    return null; // The user closed the picker.
                }

                throw e;
            }
        },

        // Devices the user granted earlier (needs the "permissions backend" in some Chrome versions).
        getDevices: async function () {
            if (!navigator.bluetooth || !navigator.bluetooth.getDevices) {
                return [];
            }

            const devices = await navigator.bluetooth.getDevices();
            devices.forEach(function (d) {
                bleDevices.set(d.id, d);
            });
            return devices.map(describeDevice);
        },

        connect: async function (id, dotnetRef) {
            const device = requireDevice(id);
            if (device.gatt.connected) {
                device.gatt.disconnect();
            }

            if (device._amiitagOnDisconnect) {
                device.removeEventListener("gattserverdisconnected", device._amiitagOnDisconnect);
            }

            device._amiitagOnDisconnect = function () {
                bleQueues.delete(id);
                dotnetRef.invokeMethodAsync("OnDisconnected").catch(function () { });
            };
            device.addEventListener("gattserverdisconnected", device._amiitagOnDisconnect);
            bleQueues.delete(id);
            await withRetries(function () { return device.gatt.connect(); });
        },

        disconnect: function (id) {
            const device = bleDevices.get(id);
            if (device && device.gatt.connected) {
                device.gatt.disconnect();
            }
        },

        hasService: function (id, service) {
            return enqueue(id, async function () {
                try {
                    await withRetries(function () { return requireDevice(id).gatt.getPrimaryService(service); });
                    return true;
                } catch {
                    return false;
                }
            });
        },

        getCharacteristic: function (id, service, characteristic) {
            return enqueue(id, async function () {
                try {
                    const value = await withRetries(async function () {
                        const primary = await requireDevice(id).gatt.getPrimaryService(service);
                        return await primary.getCharacteristic(characteristic);
                    });
                    bleCharacteristics.set(charKey(id, service, characteristic), value);
                    return true;
                } catch {
                    return false;
                }
            });
        },

        write: function (id, service, characteristic, data, withResponse) {
            return enqueue(id, async function () {
                const value = requireCharacteristic(id, service, characteristic);
                const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
                if (withResponse && value.writeValueWithResponse) {
                    await value.writeValueWithResponse(bytes);
                } else if (!withResponse && value.writeValueWithoutResponse) {
                    await value.writeValueWithoutResponse(bytes);
                } else {
                    await value.writeValue(bytes);
                }
            });
        },

        read: function (id, service, characteristic) {
            return enqueue(id, async function () {
                const view = await requireCharacteristic(id, service, characteristic).readValue();
                return new Uint8Array(view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength));
            });
        },

        startNotifications: function (id, service, characteristic, dotnetRef) {
            return enqueue(id, async function () {
                const value = requireCharacteristic(id, service, characteristic);
                if (value._amiitagHandler) {
                    value.removeEventListener("characteristicvaluechanged", value._amiitagHandler);
                }

                // Copy each value at once (the browser may reuse its buffer) and hand them to .NET strictly in order:
                // multi-packet replies are reassembled there.
                let delivery = Promise.resolve();
                value._amiitagHandler = function (event) {
                    const view = event.target.value;
                    const bytes = new Uint8Array(view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength));
                    delivery = delivery.then(function () {
                        return dotnetRef.invokeMethodAsync("OnValue", bytes);
                    }).catch(function () { });
                };
                value.addEventListener("characteristicvaluechanged", value._amiitagHandler);
                await withRetries(function () { return value.startNotifications(); });
            });
        },

        stopNotifications: function (id, service, characteristic) {
            return enqueue(id, async function () {
                const value = bleCharacteristics.get(charKey(id, service, characteristic));
                if (!value) {
                    return;
                }

                if (value._amiitagHandler) {
                    value.removeEventListener("characteristicvaluechanged", value._amiitagHandler);
                    value._amiitagHandler = null;
                }

                try {
                    await value.stopNotifications();
                } catch { }
            });
        },
    };

    // ------------------------------------------------------------------ Web Serial

    const serialPorts = new Map();
    const serialIds = new WeakMap();
    let nextSerialId = 1;

    function registerPort(port) {
        let id = serialIds.get(port);
        if (!id) {
            id = "serial-" + nextSerialId++;
            serialIds.set(port, id);
            serialPorts.set(id, port);
        }

        const info = port.getInfo ? port.getInfo() : {};
        const name = info.usbVendorId
            ? "USB serial " + info.usbVendorId.toString(16).padStart(4, "0") + ":" + (info.usbProductId || 0).toString(16).padStart(4, "0")
            : "Serial port";
        return { id: id, name: name };
    }

    function requirePort(id) {
        const port = serialPorts.get(id);
        if (!port) {
            throw new Error("Serial port not found. Add it again in Settings.");
        }

        return port;
    }

    amiitag.serial = {
        isSupported: function () {
            return "serial" in navigator;
        },

        getPorts: async function () {
            if (!("serial" in navigator)) {
                return [];
            }

            const ports = await navigator.serial.getPorts();
            return ports.map(registerPort);
        },

        requestPort: async function () {
            try {
                return registerPort(await navigator.serial.requestPort());
            } catch (e) {
                if (e && e.name === "NotFoundError") {
                    return null;
                }

                throw e;
            }
        },

        open: async function (id, baudRate, dotnetRef) {
            const port = requirePort(id);
            await port.open({ baudRate: baudRate });

            const reader = port.readable.getReader();
            port._amiitagReader = reader;

            (async function () {
                try {
                    for (;;) {
                        const result = await reader.read();
                        if (result.done) {
                            break;
                        }

                        if (result.value && result.value.length) {
                            await dotnetRef.invokeMethodAsync("OnData", result.value);
                        }
                    }
                } catch {
                    // Port closed or unplugged.
                } finally {
                    try {
                        reader.releaseLock();
                    } catch { }

                    dotnetRef.invokeMethodAsync("OnClosed").catch(function () { });
                }
            })();
        },

        write: async function (id, data) {
            const writer = requirePort(id).writable.getWriter();
            try {
                await writer.write(data);
            } finally {
                writer.releaseLock();
            }
        },

        close: async function (id) {
            const port = serialPorts.get(id);
            if (!port) {
                return;
            }

            try {
                if (port._amiitagReader) {
                    await port._amiitagReader.cancel();
                }
            } catch { }

            try {
                await port.close();
            } catch { }
        },
    };

    // ------------------------------------------------------------------ QR scanning

    // Full screen on small screens, a centred camera window elsewhere.
    function ensureQrStyles() {
        if (document.getElementById("amiitag-qr-styles")) {
            return;
        }

        const style = document.createElement("style");
        style.id = "amiitag-qr-styles";
        style.textContent = [
            ".amiitag-qr-backdrop{position:fixed;inset:0;z-index:3000;display:flex;align-items:center;justify-content:center;padding:24px;background:rgba(0,0,0,.6);}",
            ".amiitag-qr-panel{display:flex;flex-direction:column;width:min(100%,560px);max-height:100%;background:#000;border-radius:14px;overflow:hidden;box-shadow:0 10px 40px rgba(0,0,0,.4);}",
            ".amiitag-qr-panel video{display:block;width:100%;aspect-ratio:4/3;object-fit:cover;background:#000;}",
            ".amiitag-qr-panel button{border:0;background:#007aff;color:#fff;font-size:18px;min-height:52px;cursor:pointer;}",
            "@media (max-width:600px),(max-height:500px){",
            ".amiitag-qr-backdrop{padding:0;background:#000;}",
            ".amiitag-qr-panel{width:100%;height:100%;border-radius:0;box-shadow:none;}",
            ".amiitag-qr-panel video{flex:1;aspect-ratio:auto;min-height:0;}",
            ".amiitag-qr-panel button{font-size:20px;min-height:58px;padding-bottom:env(safe-area-inset-bottom);}",
            "}",
        ].join("");
        document.head.appendChild(style);
    }

    amiitag.qr = {
        isSupported: function () {
            return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        },

        // True if the browser can use the camera and at least one is connected. Device kinds are listed even before
        // camera permission is granted (only the labels are hidden), so this doesn't prompt.
        hasCamera: async function () {
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !navigator.mediaDevices.enumerateDevices) {
                return false;
            }

            try {
                const devices = await navigator.mediaDevices.enumerateDevices();
                return devices.some(function (d) { return d.kind === "videoinput"; });
            } catch {
                return false;
            }
        },

        // Shows a full screen camera view. Uses BarcodeDetector when the browser has it, otherwise asks .NET
        // (ZXing.Net) to decode frames. Resolves to the QR text or null if cancelled.
        scan: function (dotnetRef) {
            return new Promise(async function (resolve, reject) {
                ensureQrStyles();

                const overlay = document.createElement("div");
                overlay.className = "amiitag-qr-backdrop";

                const panel = document.createElement("div");
                panel.className = "amiitag-qr-panel";
                panel.setAttribute("role", "dialog");
                panel.setAttribute("aria-label", "Scan QR code");

                const video = document.createElement("video");
                video.setAttribute("playsinline", "");
                video.muted = true;

                const cancel = document.createElement("button");
                cancel.type = "button";
                cancel.textContent = "Cancel";

                panel.appendChild(video);
                panel.appendChild(cancel);
                overlay.appendChild(panel);
                document.body.appendChild(overlay);

                // Clicking the dimmed area outside the camera window cancels too.
                overlay.addEventListener("click", function (event) {
                    if (event.target === overlay) {
                        finish(null);
                    }
                });

                let stream = null;
                let done = false;
                const finish = function (value, error) {
                    if (done) {
                        return;
                    }

                    done = true;
                    if (stream) {
                        stream.getTracks().forEach(function (t) {
                            t.stop();
                        });
                    }

                    overlay.remove();
                    if (error) {
                        reject(error);
                    } else {
                        resolve(value);
                    }
                };

                cancel.addEventListener("click", function () {
                    finish(null);
                });

                try {
                    stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
                    video.srcObject = stream;
                    await video.play();
                } catch (e) {
                    finish(null, e);
                    return;
                }

                let detector = null;
                if ("BarcodeDetector" in window) {
                    try {
                        const formats = await window.BarcodeDetector.getSupportedFormats();
                        if (formats.indexOf("qr_code") >= 0) {
                            detector = new window.BarcodeDetector({ formats: ["qr_code"] });
                        }
                    } catch { }
                }

                const canvas = document.createElement("canvas");
                const context = canvas.getContext("2d", { willReadFrequently: true });

                const tick = async function () {
                    if (done) {
                        return;
                    }

                    try {
                        if (video.readyState >= 2) {
                            if (detector) {
                                const codes = await detector.detect(video);
                                if (codes.length > 0) {
                                    finish(codes[0].rawValue);
                                    return;
                                }
                            } else {
                                const scale = Math.min(1, 800 / Math.max(video.videoWidth, video.videoHeight));
                                canvas.width = Math.round(video.videoWidth * scale);
                                canvas.height = Math.round(video.videoHeight * scale);
                                context.drawImage(video, 0, 0, canvas.width, canvas.height);
                                const image = context.getImageData(0, 0, canvas.width, canvas.height);
                                const text = await dotnetRef.invokeMethodAsync("Decode", canvas.width, canvas.height, new Uint8Array(image.data.buffer));
                                if (text) {
                                    finish(text);
                                    return;
                                }
                            }
                        }
                    } catch { }

                    setTimeout(tick, detector ? 150 : 350);
                };

                tick();
            });
        },
    };
})();
