# Flask BLE Implementation Guide

Nov 19, 2026 · @Daniel Radtke

## Overview

The Flask is an Espruino (Puck.js-style nRF52) device running a JavaScript app, so you control it by sending JavaScript expressions over BLE UART and reading back one JSON line per command. There is no binary protocol: every operation is a call into globals the firmware defines (`tag`, `screen`, `require("Storage")`, `updatePosition`, `setInput`).

Everything here is derived from the official web app's bundle. The firmware it describes is Flask v8.2 on Espruino 2v08.56 or 2v16; the Slide sibling device (v6.5) speaks the same protocol with a smaller command set.

## Transport

The Flask exposes the Nordic UART Service; you write commands to TX and subscribe to RX notifications.

| Role | UUID |
| --- | --- |
| Service | `6e400001-b5a3-f393-e0a9-e50e24dcca9e` |
| TX (write, host → Flask) | `6e400002-b5a3-f393-e0a9-e50e24dcca9e` |
| RX (notify, Flask → host) | `6e400003-b5a3-f393-e0a9-e50e24dcca9e` |

Writes must be split into chunks of 20 bytes, each written only after the previous `writeValue` resolves. Notifications arrive as arbitrary fragments; decode them as Latin-1 and append to a buffer.

```javascript
const NUS = '6e400001-b5a3-f393-e0a9-e50e24dcca9e';
const TX  = '6e400002-b5a3-f393-e0a9-e50e24dcca9e';
const RX  = '6e400003-b5a3-f393-e0a9-e50e24dcca9e';

async function connect(onData) {
  const device = await navigator.bluetooth.requestDevice({
    filters: [{ services: [NUS] }], optionalServices: [NUS]
  });
  const server = await device.gatt.connect();
  const svc = await server.getPrimaryService(NUS);
  const rx = await svc.getCharacteristic(RX);
  rx.addEventListener('characteristicvaluechanged', e =>
    onData(String.fromCharCode(...new Uint8Array(e.target.value.buffer))));
  await rx.startNotifications();
  const tx = await svc.getCharacteristic(TX);
  return { device, tx };
}

async function writeRaw(tx, str) {
  for (let i = 0; i < str.length; i += 20) {
    const chunk = str.substr(i, 20);
    const buf = Uint8Array.from(chunk, c => c.charCodeAt(0));
    await tx.writeValue(buf);
  }
}
```

Outside the browser, any BLE stack works the same way (for example `bleak` in Python). On iOS, Web Bluetooth needs the WebBLE app.

## Command protocol

Wrap each expression so the Flask prints its result as one JSON line, and send commands strictly one at a time.

```
\x10Bluetooth.println(JSON.stringify(<expression>));\n
```

The `\x10` prefix tells Espruino not to echo the line. The reply is the JSON value followed by a newline, sometimes preceded by a `>` prompt or non-printable bytes.

### Parsing replies

The app parses replies like this, and you should too:

1. Ignore a fragment that is exactly `"<- Serial1\r\n>"`.
2. Append the fragment to a receive buffer.
3. Take everything up to the last `\n`; strip characters outside `0x20–0x7F`, replace `>{` with `{`, and remove newlines.
4. If that string parses as JSON (or the buffer starts with `undefined`, for void calls), it is the reply: resolve the oldest pending request with it and keep the remainder of the buffer.
5. Otherwise leave the buffer and wait for more data.

### Request queue

Only one request is in flight at a time; the next is sent after the reply arrives.

```javascript
class FlaskSession {
  constructor(tx) { this.tx = tx; this.buf = ''; this.queue = []; this.busy = false; this.onEvent = null; }

  call(expr) {
    return new Promise(resolve => { this.queue.push({ expr, resolve }); this.pump(); });
  }

  async pump() {
    if (this.busy || !this.queue.length) return;
    this.busy = true;
    await writeRaw(this.tx, `\x10Bluetooth.println(JSON.stringify(${this.queue[0].expr}));\n`);
  }

  onData(frag) {
    if (frag === '<- Serial1\r\n>') return;
    this.buf += frag;
    const nl = this.buf.lastIndexOf('\n');
    if (nl < 0) return;
    const line = this.buf.slice(0, nl).replace(/[^\x20-\x7F]/g, '').replace(/>\{/g, '{').replace(/\n/g, '');
    let value, ok = true;
    try { value = JSON.parse(line); } catch { ok = this.buf.startsWith('undefined'); value = null; }
    if (!ok) return;
    this.buf = this.buf.slice(nl + 1);
    if (value && value.event && !this.busy) { this.onEvent?.(value); return; }
    const req = this.queue.shift();
    this.busy = false;
    req?.resolve(value);
    if (value && value.event) this.onEvent?.(value);
    this.pump();
  }
}
```

The app has no timeout: a dropped reply stalls the queue until it calls `forceReset()`, which clears the buffer and queue. Add a timeout of a few seconds per request in your own code.

## Connection handshake and identification

There is no identify command; `screen.getVersion()` and `tag.getList()` together tell you what you are talking to. The app runs this sequence about 200 ms after connecting:

1. `tag.getList()`. An array means the Flask firmware is running. `"ERROR"` means the `tag` global is missing: the device is bare Espruino and needs the firmware installed (see Maintenance).
2. `global["\xff"].history = []` to clear the REPL history and free memory.
3. `tag.get()` → `{index, name, v}`: the selected tag; `v` is true in virtual-tag mode.
4. `screen.getVersion()` → identifies the device and script version.
5. `process.version` → the Espruino build.

| `screen.getVersion()` reply | Device | Type code |
| --- | --- | --- |
| `{v:"8.2", k:true\|false}` | Flask (`k` = an `.ek` key file exists) | 0 |
| `{v:"6.5", d:"1"}` | Slide | 1 |

The app forces an update when the Flask reports `v < 8.2` or the Slide `v < 6.5`, and switches to its next-gen interface when `v >= 9`. `process.version` is expected to be `2v08.56`; key upload is only sent when it is `2v16`.

A one-call probe for all three states:

```javascript
const info = await s.call('typeof screen !== "undefined" && screen.getVersion ? screen.getVersion() : "ERROR"');
// "ERROR" → unconfigured; info.d === "1" → Slide; otherwise Flask
```

The Flask holds at most `maxTags` tags; the app reads that limit from its own state, not from the device, so pick a conservative limit and stop uploads when the list reaches it.

## Tag operations

All tag operations address tags by their full stored name (see Tag naming); after any change, re-run `tag.getList()` and `tag.get()` to refresh your view.

| Operation | Command | Reply |
| --- | --- | --- |
| List | `tag.getList()` | array of names, or `"ERROR"` |
| Current tag | `tag.get()` | `{index, name, v}` |
| Select | `tag.setTag("<name>"),setInput(true)` | `{index, name, v}` |
| Download | `tag.download("<name>")` | array of 540 byte values |
| Upload | `tag.upload("<name>",<i>,<total>,"<base64>")` | `true` |
| Finish a batch early | `tag.uploadsComplete()` | `true` |
| Rename | `tag.setTag("<old>")` then `tag.rename("<old>","<new>")` | `{index, name, v}` |
| Delete | `tag.remove("<name>")` | remaining names |
| Reorder | `function(){updatePosition("<name>",<newIndex>);}()` | `""` |
| Create blank NTAG215 | `tag.createBlank()` | `undefined` |

### Uploading

Upload sends a whole dump base64-encoded in one command, numbered 1 to `total` within a batch.

- Accept files of 180 bytes or less, 532–574 bytes, 1024 bytes or 2048 bytes; reject anything else.
- The first call (`i == 1`) disables the buttons and shows the upload screen; the call where `i == total` appends the new tags to `.order`, re-enables input and refreshes the screen.
- If you stop before `i == total` (for example, the Flask is full), send `tag.uploadsComplete()` so the device leaves upload mode.

```javascript
async function uploadTags(s, files /* [{name, bytes: Uint8Array}] */) {
  for (let i = 0; i < files.length; i++) {
    const b64 = btoa(String.fromCharCode(...files[i].bytes));
    await s.call(`tag.upload("${files[i].name}",${i + 1},${files.length},"${b64}")`);
  }
  return s.call('tag.getList()');
}
```

### Downloading

```javascript
const bytes = new Uint8Array(await s.call(`tag.download("${name}")`));
```

### Renaming

The tag must be selected before `tag.rename`, because the firmware re-stores the currently loaded tag data under the new name. The app runs `require('Storage').compact()` after every tenth rename to stop flash filling with erased records.

## Tag naming and file format

Each tag is a 540-byte NTAG215 dump stored as one Storage file named `<display name>|<amiibo tail in base36>|<set key>`, at most 27 characters.

| Part | Source | Example |
| --- | --- | --- |
| Display name | Filename or AmiiboAPI name; `_` → space, only `A–Z a–z 0–9 - space` kept, accents stripped | `Mario` |
| Tail | Bytes 88–91 of the dump as hex, written in base36 | `00000002` → `2` |
| Set key | 4-character set id, or `0` when the tag is in no set | `0` |

Build names the way the app does, so its UI and sets recognise your tags:

```javascript
function tagName(bytes, fallback, setKey = 0, existing = []) {
  const tail = [...bytes.slice(88, 92)].map(b => b.toString(16).padStart(2, '0')).join('');
  let base = `${fallback}|${parseInt(tail, 16).toString(36)}`;
  if (base.length > 22) base = fallback.slice(0, fallback.length - (base.length - 22)) + base.slice(base.indexOf('|'));
  const fit = n => n.length > 27 ? n.split('|')[0].slice(0, 27 - (n.length - n.split('|')[0].length)) + n.slice(n.indexOf('|')) : n;
  let name = fit(`${base}|${setKey}`), r = 1;
  while (existing.includes(name)) name = fit(`${r++}. ${base}|${setKey}`); // de-duplicate
  return name;
}
```

To turn a name back into an amiibo id, read the part after the first `|`, parse it as base36 and left-pad the hex to 8 digits; match that against the AmiiboAPI `tail` field. Tags whose second part is missing or shorter than 4 characters show as unknown NFC tags.

Files beginning with a dot are firmware state, not tags. Do not write tag names that start with a dot.

| File | Holds |
| --- | --- |
| `.order` | `Uint8Array` of Storage-list indexes giving the display order |
| `.sort` | `0` (insertion order) or `1` (A–Z) |
| `.ek` | Amiibo master keys, enabling UID randomisation |
| `.bootcde` | The Flask firmware script |
| `.config` | Device settings, erased on a restore install |

## Maintenance

Maintenance commands are multi-line functions sent as a single immediately-invoked expression; the destructive ones cannot be undone, so back up first.

| Task | Command | Notes |
| --- | --- | --- |
| Battery | `Puck.getBatteryPercentage()` | 0–100 |
| Compact storage | `require('Storage').compact()` | Run after bulk deletes or renames |
| Change screen face | `screen.setFace("0")` or `"1"` | Flask only |
| Store master keys | `require("Storage").write('.ek', atob("<base64 key file>"))` | App sends it only on Espruino 2v16 and Flask v7+ |

### Change sort order

```javascript
function changeSort() {
  require("Storage").write('.sort', "1");   // 1 = A–Z, 0 = last update
  sortConfig = 1;
  setInput(false);
  tag.refreshTaglist();                      // only when tags exist
}()
```

Input stays disabled afterwards. The app follows with `tag.getList()` and `tag.setTag("<first>"),setInput(true)` to re-select the first tag and re-enable the buttons.

### Delete all tags

```javascript
function deleteAll() {
  global["\xff"].history = [];
  var s = require("Storage");
  tag.tagStorage._storage.list(/^[a-zA-Z0-9]/).forEach(s.erase);
  s.erase('.sort');
  tag.tagList = tag.tagStorage.getAllTags();
  tag.reload();
  if (tag.tagList.length === 0) { tag._screen.splash(); }
  tag.curTag = 0;
  require('Storage').compact();
  Bluetooth.println(JSON.stringify({event: 'deleteAllEnd'}));
  return {event: 'deleteAllE'};
}()
```

To delete only one set's tags, erase the files whose name contains `|<setKey>` and rebuild `.order` without their indexes, shifting larger indexes down by the number removed; then reload as above.

### Factory reset

```javascript
function deleteAll() {
  var s = require("Storage");
  s.eraseAll(); s.compact(); reset();
  Bluetooth.println(JSON.stringify({event: 'factoryResetEnd'}));
  return {event: 'factoryResetEnd'};
}()
```

This erases the firmware too; the device comes back as bare Espruino and needs a firmware install.

### Back up the firmware

```javascript
function downloadBootCode() {
  var s = require("Storage");
  var idx = 0;
  var block = s.read('.bootcde', idx, 512);
  Bluetooth.println(JSON.stringify({event: 'backupStart', data: block}));
  idx += 512;
  block = s.read('.bootcde', idx, 512); idx += 512;
  while (block != '') {
    Bluetooth.println(JSON.stringify({event: 'backup', data: block}));
    block = s.read('.bootcde', idx, 512); idx += 512;
  }
  return {event: 'backupEnd'};
}()
```

Concatenate the `data` fields from `backupStart` and every `backup` line until `backupEnd`, and save the result (the app names it `backup-YYYY-M-D.flask.hex`).

### Install or update firmware

Split the firmware text into 384-byte chunks and write each with its offset, then load it.

1. Call your session's reset so no stale request is pending.
2. For chunk `i` at offset `384 × i`, send:

```javascript
function writeFile() {
  // chunk 0 only:
  require("Storage").compact(); require("Storage").erase(".bootcde");
  if (global.leftWatch) { clearWatch(global.leftWatch); clearWatch(global.middleWatch); clearWatch(global.rightWatch); }
  global["\xff"].history = [];
  // restore install only: require("Storage").erase(".config"); require("Storage").erase(".sort");
  require("Storage").write('.bootcde', atob("<chunk base64>"), <offset>, <totalLength>)
}()
```

3. After the last chunk, send the finaliser; the device reloads and drops the connection.

```javascript
function endFirmware() {
  Bluetooth.println(JSON.stringify({event: 'endFirmware'}));
  global["\xff"].history = [];
  load();
  NRF.disconnect();
}()
```

The Flask v8.2 and Slide v6.5 scripts are the `yi` and `fi` string constants in the web app bundle. Tags are not lost by a normal update. If the update fails, the screen stays blank (restore mode): charge for 30 minutes, power on, reconnect and install again.

## Device events

The Flask also prints JSON lines on its own, each with an `event` field; route these to a handler instead of treating them as replies.

| Event | When | Fields | Suggested action |
| --- | --- | --- | --- |
| `button` | Left or right button changed the tag | `index`, `name`, `v` | Update the selected tag |
| `delete` | Tag deleted from the on-device menu | — | Refresh list and current tag |
| `refresh` | A game rewrote a tag and its name changed | — | Refresh list and current tag |
| `backupStart` / `backup` | Firmware backup streaming | `data` | Append `data` |
| `backupEnd` | Backup finished (the command's own reply) | — | Save the file |
| `deleteAllEnd` | Delete-all finished | — | Refresh |
| `factoryResetEnd` | Factory reset finished | — | Expect bare Espruino |
| `endFirmware` | Firmware written, about to reload | — | Expect a disconnect |

## Gotchas

Most failures come from overlapping commands, unescaped names and memory pressure on the device.

- **One command at a time.** The REPL interleaves output if you pipeline; always wait for the reply.
- **Events can steal replies.** The web app pops the oldest pending request on any JSON line, so a `button` press mid-request gets taken as that request's reply. Check for an `event` field first, as the session class above does.
- **Escape names.** Names are spliced into JavaScript source inside double quotes. Sanitise to `A–Z a–z 0–9 - space` and `|` before sending, or use `JSON.stringify(name)` when building the expression.
- **Clear REPL history.** Send `global["\xff"].history = []` after listing and after bulk work; the device has little RAM and the history grows with every long command.
- **Compact flash.** Erased files keep using space until `require('Storage').compact()`; run it after bulk deletes and every tenth rename.
- **Keep `.order` consistent.** Add or remove tags only through `tag.upload`, `tag.remove` and `tag.createBlank`, which maintain it. If you erase tag files yourself, rebuild `.order` as described under Maintenance.
- **Re-enable input.** `changeSort` and an interrupted upload leave the buttons disabled; finish with `setInput(true)` (included in the select command) or `tag.uploadsComplete()`.
- **Add timeouts.** The app waits forever on a reply; a dropped notification stalls everything. Time out after about 5 seconds, then clear the buffer and queue.
- **Low battery.** Below 3.1 V the Flask shows a charge prompt, disables input and puts Bluetooth to sleep, so commands stop working.
