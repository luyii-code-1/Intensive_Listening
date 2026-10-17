import { test } from 'node:test';
import assert from 'node:assert/strict';
import { nextVersion, bumpSource, readVersion } from '../scripts/bump-web-version.mjs';

const SW = "// Version: 1.0.0 (Offline PWA)\nconst CACHE_NAME = 'il-pwa-1.5.0+16';\n";
const HTML = [
  '<a class="app-version-badge" title="版本: 1.5.0+16 (点击查看源码)">',
  '  <span class="version-text">1.5.0+16</span>',
].join('\n');
const APP = "      app_version: '1.5.0+16',\n";

test('nextVersion increments only the build number', () => {
  assert.equal(nextVersion('1.5.0+16'), '1.5.0+17');
  assert.equal(nextVersion('1.5.0'), '1.5.0+1');
  assert.equal(nextVersion('2.2.0+3'), '2.2.0+4');
  assert.throws(() => nextVersion('nightly'), /无法识别的版本号/);
});

test('bumpSource rewrites the service worker cache name', () => {
  const updated = bumpSource(SW, 'sw.js', '17');
  assert.match(updated, /CACHE_NAME = 'il-pwa-1\.5\.0\+17'/);
  assert.match(updated, /Version: 1\.0\.0 \(Offline PWA\)/, '顶部注释里的无关版本号不受影响');
});

test('bumpSource rewrites both index.html occurrences', () => {
  const updated = bumpSource(HTML, 'index.html', '17');
  assert.match(updated, /title="版本: 1\.5\.0\+17 \(点击查看源码\)"/);
  assert.match(updated, /<span class="version-text">1\.5\.0\+17<\/span>/);
  assert.equal(updated.match(/1\.5\.0\+16/g), null, '旧版本号不应残留');
});

test('bumpSource rewrites js/app.js', () => {
  const updated = bumpSource(APP, 'js/app.js', '17');
  assert.match(updated, /app_version: '1\.5\.0\+17',/);
});

test('bumpSource adds a build number when none is present', () => {
  const bare = "const CACHE_NAME = 'il-pwa-1.4.2';\n";
  assert.match(bumpSource(bare, 'sw.js', '1'), /il-pwa-1\.4\.2\+1/);
});

test('bumpSource refuses files it has no rule for', () => {
  assert.throws(() => bumpSource('x', 'css/fluent.css', '1'), /没有 css\/fluent\.css 的版本改写规则/);
});

test('readVersion round-trips with bumpSource output', () => {
  const bumped = bumpSource(SW, 'sw.js', '17');
  assert.equal(readVersion(bumped), '1.5.0+17');
  assert.throws(() => readVersion("const CACHE_NAME = 'il-pwa';"), /找不到 CACHE_NAME/);
});