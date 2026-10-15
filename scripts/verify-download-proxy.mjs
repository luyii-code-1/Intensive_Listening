import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { onRequest } from '../edge-functions/downloads/[tag]/[file].js';

// With a deployment URL, exercise the deployed route instead of the local handler.
const base = process.argv[2];
const files = [
  ['Intensive-Listening-2.1.0-win-x64-Setup.exe', 154540989, '3c5db26d8d24c4d424fa1e913bb3698b2a012676f4ee63217d06fd63e8107b86'],
  ['Intensive-Listening-2.1.0-osx-arm64.dmg', 123101025, '849d556da6f89e2ab4a3517a01437458695b89880f744c04833a108b64bad0e1'],
];
async function send(name, init) {
  const request = new Request(`${base || 'https://il.luyii.cn'}/downloads/v2.1.0/${name}`, {
    ...init, signal: AbortSignal.timeout(180_000),
  });
  return base ? fetch(request, { redirect: 'manual' }) : onRequest({ request });
}
try {
  for (const [name, expectedSize, expectedHash] of files) {
    console.log(`${name}: checking HEAD`);
    const head = await send(name, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(Number(head.headers.get('Content-Length')), expectedSize);
    console.log(`${name}: checking Range`);
    const partial = await send(name, { headers: { Range: 'bytes=1024-2047' } });
    assert.equal(partial.status, 206);
    assert.equal(partial.headers.get('Content-Range'), `bytes 1024-2047/${expectedSize}`);
    assert.equal((await partial.arrayBuffer()).byteLength, 1024);
    console.log(`${name}: checking full download and SHA-256`);
    const response = await send(name);
    assert.equal(response.status, 200);
    const hash = createHash('sha256');
    let size = 0;
    const started = performance.now();
    for await (const chunk of response.body) {
      size += chunk.byteLength;
      hash.update(chunk);
    }
    assert.equal(size, expectedSize);
    assert.equal(hash.digest('hex'), expectedHash);
    console.log(JSON.stringify({ name, size, sha256: 'PASS', head: 'PASS', range: 'PASS', seconds: Math.round((performance.now() - started) / 1000), target: base || 'local handler → GitHub' }));
  }
} catch (error) {
  console.error(`Download verification failed: ${error.code || error.name}: ${error.message}`);
  process.exitCode = 1;
}
