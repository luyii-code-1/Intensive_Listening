import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('../release.js', import.meta.url), 'utf8');
const DEFAULT_TAG = /DEFAULT_TAG\s*=\s*'([^']+)'/.exec(source)[1];
const DEFAULT_VERSION = DEFAULT_TAG.replace(/^v/, '');

const WINDOWS = 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.0/Intensive-Listening-2.1.0-win-x64-Setup.exe';
const MACOS = 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.0/Intensive-Listening-2.1.0-osx-arm64.dmg';
const BASELINE = { windows: 'https://example.test/baseline-win.exe', macos: 'https://example.test/baseline-mac.dmg' };

function release(overrides = {}) {
  return {
    tag: 'v2.1.0',
    assets: {
      windows: { filename: 'Intensive-Listening-2.1.0-win-x64-Setup.exe', path: 'downloads/Intensive-Listening-2.1.0-win-x64-Setup.exe', size: 154540989, sha256: 'a'.repeat(64), source: WINDOWS },
      macos: { filename: 'Intensive-Listening-2.1.0-osx-arm64.dmg', path: 'downloads/Intensive-Listening-2.1.0-osx-arm64.dmg', size: 123101025, sha256: 'b'.repeat(64), source: MACOS },
    },
    ...overrides,
  };
}

// 在沙箱里跑 release.js，附带最小 DOM 桩；脚本末尾的 switchDownloads() 即完成值。
async function run({ metadata = release(), head = () => true, body = () => null } = {}) {
  const links = Object.fromEntries(Object.entries(BASELINE).map(([platform, href]) => [platform, { href, download: '' }]));
  const meta = { textContent: '' };
  const probes = [];

  const context = vm.createContext({
    console,
    document: {
      querySelector: (selector) => /^\[data-download-platform="(\w+)"\]$/.test(selector) ? links[RegExp.$1] ?? null : null,
      getElementById: (id) => id === 'release-download-meta' ? meta : null,
    },
    fetch: async (url, options = {}) => {
      if (String(url) === 'downloads/latest.json') {
        return metadata === null
          ? new Response('', { status: 404 })
          : new Response(metadata === 'malformed' ? '{not json' : JSON.stringify(metadata), { status: 200 });
      }
      probes.push(String(url));
      assert.equal(options.method, 'HEAD');
      return new Response(body(String(url)), { status: head(String(url)) ? 200 : 502 });
    },
  });

  await vm.runInContext(source, context);
  return { links, meta, probes };
}

test('routes both platforms through the first reachable mirror', async () => {
  const { links, meta, probes } = await run();
  assert.equal(links.windows.href, `https://gh-proxy.org/${WINDOWS}`);
  assert.equal(links.macos.href, `https://gh-proxy.org/${MACOS}`);
  assert.equal(links.windows.download, 'Intensive-Listening-2.1.0-win-x64-Setup.exe');
  assert.equal(meta.textContent, 'v2.1.0 正式版，境内加速下载');
  assert.equal(probes.length, 1, '探测到一个可用节点后就不再试下一个');
});

test('falls through to the next mirror when a node cannot serve the file', async () => {
  const { links, meta } = await run({ head: (url) => !url.startsWith('https://gh-proxy.org/') });
  assert.equal(links.windows.href, `https://v4.gh-proxy.org/${WINDOWS}`);
  assert.equal(links.macos.href, `https://v4.gh-proxy.org/${MACOS}`);
  assert.equal(meta.textContent, 'v2.1.0 正式版，境内加速下载');
});

test('falls back to GitHub direct links when every mirror is unreachable', async () => {
  const { links, meta } = await run({ head: () => false });
  assert.equal(links.windows.href, WINDOWS);
  assert.equal(links.macos.href, MACOS);
  assert.equal(meta.textContent, 'v2.1.0 正式版，通过 GitHub 下载');
});

test('derives both platform links from DEFAULT_TAG when metadata is absent', async () => {
  const { links, meta } = await run({ metadata: null });
  assert.equal(links.windows.href, `https://gh-proxy.org/https://github.com/luyii-code-1/Intensive_Listening/releases/download/${DEFAULT_TAG}/Intensive-Listening-${DEFAULT_VERSION}-win-x64-Setup.exe`);
  assert.equal(links.macos.href, `https://gh-proxy.org/https://github.com/luyii-code-1/Intensive_Listening/releases/download/${DEFAULT_TAG}/Intensive-Listening-${DEFAULT_VERSION}-osx-arm64.dmg`);
  assert.equal(meta.textContent, `${DEFAULT_TAG} 正式版，境内加速下载`);
});

test('derives both platform links from DEFAULT_TAG when metadata is malformed', async () => {
  const { links } = await run({ metadata: 'malformed' });
  assert.match(links.windows.href, /Intensive-Listening-\d+\.\d+\.\d+(?:\.\d+)?-win-x64-Setup\.exe$/);
  assert.match(links.macos.href, /Intensive-Listening-\d+\.\d+\.\d+(?:\.\d+)?-osx-arm64\.dmg$/);
});

test('rejects metadata whose asset source is not a known Release URL', async () => {
  const tampered = release();
  tampered.assets.windows.source = 'https://evil.test/Intensive-Listening-2.1.0-win-x64-Setup.exe';
  const { links, probes } = await run({ metadata: tampered });
  assert.match(links.windows.href, new RegExp(`/download/${DEFAULT_TAG}/`), '退回 DEFAULT_TAG，而不是篡改后的来源');
  assert.equal(probes.length, 1);
  assert.ok(probes[0].startsWith('https://gh-proxy.org/https://github.com/luyii-code-1/'));
});

test('ignores a metadata tag that is not a stable v2 release', async () => {
  const { meta } = await run({ metadata: release({ tag: 'v1.9.0' }) });
  assert.equal(meta.textContent, `${DEFAULT_TAG} 正式版，境内加速下载`);
});

test('keeps the version in build output in step with DEFAULT_TAG', async () => {
  const html = await readFile(new URL('../index.html', import.meta.url), 'utf8');
  assert.ok(html.includes('__VERSION__'), 'index.html 里保留 __VERSION__ 占位符，由构建脚本替换');
  assert.ok(!/download\/v2\.\d+\.\d+\//.test(html), 'index.html 不应再写死版本号');
});

test('uses four-component release download metadata', async () => {
  const metadata = JSON.parse(JSON.stringify(release()).replaceAll('2.1.0', '2.1.2.3'));
  const { links, meta } = await run({ metadata });
  assert.match(links.windows.href, /v2\.1\.2\.3\/Intensive-Listening-2\.1\.2\.3-win-x64-Setup\.exe$/);
  assert.match(meta.textContent, /v2\.1\.2\.3/);
});
