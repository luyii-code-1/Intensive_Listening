import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseWebVersion, renderWebJson } from '../scripts/sync-web-version.mjs';

test('reads the version from the service worker cache name', () => {
  assert.equal(parseWebVersion("const CACHE_NAME = 'il-pwa-1.5.0+16';"), '1.5.0+16');
  assert.equal(parseWebVersion("const CACHE_NAME = 'il-pwa-2.2.0+3';"), '2.2.0+3');
  assert.equal(parseWebVersion("const CACHE_NAME = 'il-pwa-1.0.1+9';"), '1.0.1+9');
});

test('accepts a version without a build number', () => {
  assert.equal(parseWebVersion("const CACHE_NAME = 'il-pwa-1.4.2';"), '1.4.2');
});

test('rejects a service worker without a recognisable cache name', () => {
  assert.throws(() => parseWebVersion("const CACHE_NAME = 'il-pwa';"), /找不到 CACHE_NAME/);
  assert.throws(() => parseWebVersion("const OTHER = 'x';"), /找不到 CACHE_NAME/);
  assert.throws(() => parseWebVersion("const CACHE_NAME = 'il-pwa-nightly';"), /无法识别的 Web 播放器版本/);
});

test('renderWebJson emits the shape build.mjs reads back', () => {
  const rendered = renderWebJson('1.5.0+16');
  assert.equal(JSON.parse(rendered).webPlayer, '1.5.0+16');
  assert.match(rendered, /"webPlayer"\s*:\s*"1\.5\.0\+16"/, 'build.mjs 依赖这个键名与格式');
  assert.ok(rendered.endsWith('\n'));
});