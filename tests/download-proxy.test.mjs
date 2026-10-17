import { test } from 'node:test';
import assert from 'node:assert/strict';
import { onRequest } from '../edge-functions/downloads/[tag]/[file].js';

const url = 'https://il.luyii.cn/downloads/v2.1.0/Intensive-Listening-2.1.0-win-x64-Setup.exe';
test('rejects unrelated paths and methods before contacting upstream', async () => {
  for (const path of ['/downloads/v2.1.0/other.exe', '/downloads/v2.1.0/Intensive-Listening-2.0.0-win-x64-Setup.exe']) {
    assert.equal((await onRequest({ request: new Request(new URL(path, url)) })).status, 404);
  }
  assert.equal((await onRequest({ request: new Request(url, { method: 'POST' }) })).status, 405);
});

test('follows GitHub redirects and preserves partial download semantics', async (t) => {
  const calls = [];
  t.mock.method(globalThis, 'fetch', async (target, options) => {
    calls.push({ target: String(target), options });
    return calls.length === 1
      ? new Response(null, { status: 302, headers: { Location: 'https://release-assets.githubusercontent.com/asset?token=temporary' } })
      : new Response(new Uint8Array([1, 2, 3]), { status: 206, headers: {
        'Content-Range': 'bytes 0-2/100', 'Content-Length': '3', 'Accept-Ranges': 'bytes',
        'Set-Cookie': 'private=value',
      } });
  });
  const response = await onRequest({ request: new Request(url, { headers: { Range: 'bytes=0-2' } }) });
  assert.equal(response.status, 206);
  assert.equal(calls.length, 2);
  assert.equal(calls[1].options.headers.get('Range'), 'bytes=0-2');
  assert.equal(response.headers.get('Content-Range'), 'bytes 0-2/100');
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
  assert.equal(response.headers.get('Location'), null);
  assert.equal(response.headers.get('Set-Cookie'), null);
  assert.deepEqual([...new Uint8Array(await response.arrayBuffer())], [1, 2, 3]);
});

test('rejects redirects to other origins', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response(null, {
    status: 302, headers: { Location: 'https://example.com/file' },
  }));
  assert.equal((await onRequest({ request: new Request(url) })).status, 502);
});

test('HEAD preserves file size and has no body; upstream errors are not cached', async (t) => {
  t.mock.method(globalThis, 'fetch', async (_, options) => {
    assert.equal(options.method, 'HEAD');
    return new Response(null, { headers: { 'Content-Length': '154540989' } });
  });
  const response = await onRequest({ request: new Request(url, { method: 'HEAD' }) });
  assert.equal(response.headers.get('Content-Length'), '154540989');
  assert.equal(response.body, null);
  t.mock.restoreAll();
  t.mock.method(globalThis, 'fetch', async () => new Response('missing', { status: 404 }));
  const missing = await onRequest({ request: new Request(url) });
  assert.equal(missing.status, 404);
  assert.equal(missing.headers.get('Cache-Control'), 'no-store');
});


test('serves matching four-component builds and rejects mismatched filenames', async (t) => {
  const target = url.replaceAll('2.1.0', '2.1.2.3');
  t.mock.method(globalThis, 'fetch', async upstream => {
    assert.equal(String(upstream), 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.2.3/Intensive-Listening-2.1.2.3-win-x64-Setup.exe');
    return new Response(null, { headers: { 'Content-Length': '123' } });
  });
  assert.equal((await onRequest({ request: new Request(target, { method: 'HEAD' }) })).status, 200);
  const mismatch = target.replace('Intensive-Listening-2.1.2.3-', 'Intensive-Listening-2.1.2.4-');
  assert.equal((await onRequest({ request: new Request(mismatch) })).status, 404);
});
