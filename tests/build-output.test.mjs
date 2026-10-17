// 构建产物必须真的能支撑 release.js 的运行时约定，否则线上会静默退回硬编码链接。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);
const root = fileURLToPath(new URL('../', import.meta.url));

test('build.mjs stamps the version and ships downloads/latest.json', async () => {
  await run(process.execPath, ['scripts/build.mjs'], { cwd: root });

  const html = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
  assert.ok(!html.includes('__VERSION__'), 'dist/index.html 不应残留 __VERSION__');
  assert.match(html, /download\/v\d+\.\d+\.\d+\/Intensive-Listening-\d+\.\d+\.\d+-win-x64-Setup\.exe/);

  const metadata = JSON.parse(await readFile(new URL('../dist/downloads/latest.json', import.meta.url), 'utf8'));
  assert.match(metadata.tag, /^v2\.\d+\.\d+$/);
  assert.ok(metadata.assets.windows.source.endsWith(metadata.assets.windows.filename));
  assert.ok(metadata.assets.macos.source.endsWith(metadata.assets.macos.filename));

  // release.js 用相对路径取它，因此必须落在 dist 根下。
  const statted = await stat(new URL('../dist/downloads/latest.json', import.meta.url));
  assert.ok(statted.isFile());

  // 版本显示要与 latest.json 的 tag 同源。
  const version = metadata.tag.replace(/^v/, '');
  assert.ok(html.includes(`${version} 正式版`), `dist/index.html 应显示 ${version}`);
});

test('build.mjs stamps the Web player version from web.json', async () => {
  await run(process.execPath, ['scripts/build.mjs'], { cwd: root });

  const html = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
  assert.ok(!html.includes('__WEB_VERSION__'), 'dist/index.html 不应残留 __WEB_VERSION__');

  const { webPlayer } = JSON.parse(await readFile(new URL('../web.json', import.meta.url), 'utf8'));
  assert.match(webPlayer, /^\d+\.\d+\.\d+(\+\d+)?$/, 'web.json 里的版本格式');
  assert.ok(html.includes(`当前版本 ${webPlayer}`), `dist/index.html 应显示 Web 播放器 ${webPlayer}`);
  assert.ok(!html.includes('当前版本 1.1.0'), '不应再出现写死的旧版本号');
});