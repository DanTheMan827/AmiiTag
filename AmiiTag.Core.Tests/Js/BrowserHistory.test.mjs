import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
const source = await readFile(new URL('../../AmiiTag.WebAssembly/wwwroot/js/amiitag.js', import.meta.url), 'utf8');

function fixture() {
    const calls = [];
    const window = { history: {
        go: delta => calls.push(['go', delta]),
        pushState: () => assert.fail('Back must not append a route'),
        replaceState: () => assert.fail('Back must not replace a route')
    } };
    vm.runInNewContext(source, { window });
    return { calls, app: window.amiitag };
}

test('Back traverses exactly one existing entry without rewriting history', () => {
    const f = fixture(); f.app.goHistory(-1);
    assert.deepEqual(f.calls, [['go', -1]]);
});

test('PopTo traverses the requested distance without rewriting history', () => {
    const f = fixture(); f.app.goHistory(-3);
    assert.deepEqual(f.calls, [['go', -3]]);
});
