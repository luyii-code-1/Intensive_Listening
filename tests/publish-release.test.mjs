import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildMetadata, syncFallbackTag } from '../scripts/publish-release.mjs';

const DIGEST = 'sha256:' + 'a'.repeat(64);

function release(overrides = {}) {
  return {
    tag_name: 'v2.2.0',
    name: 'Intensive Listening 2 Resonance · 2.2.0',
    draft: false,
    prerelease: false,
    assets: [
      { name: 'Intensive-Listening-2.2.0-win-x64-Setup.exe', size: 154540989, digest: DIGEST, browser_download_url: 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.2.0/Intensive-Listening-2.2.0-win-x64-Setup.exe' },
      { name: 'Intensive-Listening-2.2.0-osx-arm64.dmg', size: 123101025, digest: DIGEST, browser_download_url: 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.2.0/Intensive-Listening-2.2.0-osx-arm64.dmg' },
      { name: 'SHA256SUMS.txt', size: 476, digest: DIGEST, browser_download_url: 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.2.0/SHA256SUMS.txt' },
    ],
    ...overrides,
  };
}

test('derives both platform assets from the release tag', () => {
  const metadata = buildMetadata(release());
  assert.equal(metadata.tag, 'v2.2.0');
  assert.equal(metadata.assets.windows.filename, 'Intensive-Listening-2.2.0-win-x64-Setup.exe');
  assert.equal(metadata.assets.windows.path, 'downloads/Intensive-Listening-2.2.0-win-x64-Setup.exe');
  assert.equal(metadata.assets.windows.size, 154540989);
  assert.equal(metadata.assets.windows.sha256, 'a'.repeat(64));
  assert.equal(metadata.assets.macos.filename, 'Intensive-Listening-2.2.0-osx-arm64.dmg');
});

test('rejects drafts, prereleases and non-stable tags', () => {
  for (const bad of [{ draft: true }, { prerelease: true }, { tag_name: 'v2.2.0-beta' }, { tag_name: 'v2.2' }, { tag_name: 'nightly' }]) {
    assert.throws(() => buildMetadata(release(bad)), /draft or prerelease|stable 2\.x tag/);
  }
});

test('rejects an asset whose URL does not belong to this repository release', () => {
  const tampered = release();
  tampered.assets[0].browser_download_url = 'https://evil.test/Intensive-Listening-2.2.0-win-x64-Setup.exe';
  assert.throws(() => buildMetadata(tampered), /Unexpected source URL/);
});

test('rejects assets without a published SHA-256 or size', () => {
  const missingDigest = release();
  delete missingDigest.assets[0].digest;
  assert.throws(() => buildMetadata(missingDigest), /Missing SHA-256/);

  const missingSize = release();
  missingSize.assets[0].size = 0;
  assert.throws(() => buildMetadata(missingSize), /Missing size/);
});

test('requires exactly one asset per platform', () => {
  const duplicated = release();
  duplicated.assets.push(duplicated.assets[0]);
  assert.throws(() => buildMetadata(duplicated), /exactly one windows asset/);
});

test('syncFallbackTag rewrites only the fallback version constant', () => {
  const source = [
    "const DEFAULT_TAG = 'v2.1.0';",
    "// 注释里出现的 v2.1.0 不应被改动",
    "const OTHER = 'v9.9.9';",
  ].join('\n');
  const updated = syncFallbackTag(source, 'v2.2.0');
  assert.match(updated, /const DEFAULT_TAG = 'v2\.2\.0';/);
  assert.match(updated, /注释里出现的 v2\.1\.0 不应被改动/, '注释保持原样');
  assert.match(updated, /const OTHER = 'v9\.9\.9';/);
  assert.equal(updated.match(/v2\.1\.0/g)?.length, 1, '只剩注释里那一处未被改写');
});

test('syncFallbackTag fails loudly when release.js has no DEFAULT_TAG', () => {
  assert.throws(() => syncFallbackTag('const OTHER = 1;', 'v2.2.0'), /DEFAULT_TAG not found/);
});