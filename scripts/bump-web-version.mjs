// 递增 Web 播放器（web-edition 分支）的构建号，并同步版本串与提交标识。
//
// 版本形如 1.5.0+16；只动 +N，不动 1.x.y 主版本——主版本表达面向用户的功能/
// 兼容层级，由人决定；+N 只管缓存失效与构建溯源，交给 CI。
//
// sw.js 的 CACHE_NAME 必须与版本同批改：资源是 cache-first 取用的，旧缓存只在
// activate 里按 CACHE_NAME 清理，所以改了 js/css 却不改它，用户会一直拿旧代码。
//
// 用法：node scripts/bump-web-version.mjs <webedition 目录> [提交标识]
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

// 必须同时更新的位置（index.html 有两处），漏一就会出现「显示新版本却仍在跑旧缓存」的错配。
// 每条规则捕获 (前缀)(旧值)(后缀)，只替换中间那段，避免吃掉引号或标签。
const VERSION = String.raw`\d+\.\d+\.\d+(?:\+\d+)?`;
const TARGETS = [
  { file: 'sw.js', pattern: new RegExp(String.raw`(CACHE_NAME\s*=\s*'il-pwa-)(${VERSION})(')`) },
  { file: 'index.html', pattern: new RegExp(String.raw`(<span class="version-text">)(${VERSION})(</span>)`) },
  { file: 'index.html', pattern: new RegExp(String.raw`(title="版本: )(${VERSION})( \(点击查看源码\)")`) },
  { file: 'js/app.js', pattern: new RegExp(String.raw`(app_version:\s*')(${VERSION})(')`) },
];

// 版本徽章里并排显示的提交标识。历史填过 c38d937 / 6d0f5ac / 25f9062，
// 后来变成手写的 winui3 占位。它不参与缓存，但写死会腐坏，因此 CI 一并填自身 SHA。
const COMMIT_TARGET = {
  file: 'index.html',
  pattern: /(<span class="commit-text">)([^<]*)(<\/span>)/,
};

export function nextVersion(current) {
  const match = /^(\d+\.\d+\.\d+)(?:\+(\d+))?$/.exec(current);
  if (!match) throw new Error(`无法识别的版本号：${current}`);
  return `${match[1]}+${(match[2] ? Number(match[2]) : 0) + 1}`;
}

function rewrite(source, pattern, value) {
  return source.replace(pattern, (_all, before, _old, after) => `${before}${value}${after}`);
}

// 改写某个文件的版本串；返回改写后的文本。
export function bumpSource(source, file, build) {
  const targets = TARGETS.filter((t) => t.file === file);
  if (targets.length === 0) throw new Error(`没有 ${file} 的版本改写规则`);
  return targets.reduce(
    (text, { pattern }) => text.replace(pattern, (_all, before, version, after) =>
      `${before}${nextVersion(version).replace(/\+.*$/, `+${build}`)}${after}`),
    source,
  );
}

// 写入提交标识；未提供标识时原样返回（保留现有值）。
export function stampCommit(source, commit) {
  if (!commit) return source;
  if (!/^[0-9a-f]{7,40}$/.test(commit)) throw new Error(`无法识别的提交标识：${commit}`);
  return rewrite(source, COMMIT_TARGET.pattern, commit.slice(0, 7));
}

export function readVersion(serviceWorkerSource) {
  const match = /CACHE_NAME\s*=\s*'il-pwa-(\d+\.\d+\.\d+(?:\+\d+)?)'/.exec(serviceWorkerSource);
  if (!match) throw new Error("sw.js 里找不到 CACHE_NAME = 'il-pwa-<版本>'");
  return match[1];
}

export function readCommit(html) {
  const match = /<span class="commit-text">([^<]*)<\/span>/.exec(html);
  if (!match) throw new Error('index.html 里找不到 <span class="commit-text">');
  return match[1];
}

async function main() {
  const args = process.argv.slice(2);
  // --print-version：只打印当前版本，不改动任何文件（CI 用来记录 bump 前后值）。
  const printOnly = args[0] === '--print-version';
  const [directory, commit] = printOnly ? args.slice(1) : args;
  if (!directory) throw new Error('usage: bump-web-version.mjs [--print-version] <webedition 目录> [提交标识]');

  const read = (file) => readFile(join(directory, file), 'utf8');
  const current = readVersion(await read('sw.js'));
  if (printOnly) {
    console.log(current);
    return;
  }
  const updated = nextVersion(current);
  const build = updated.split('+')[1];

  for (const file of [...new Set(TARGETS.map((t) => t.file))]) {
    const source = await read(file);
    const bumped = file === 'index.html'
      ? stampCommit(bumpSource(source, file, build), commit)
      : bumpSource(source, file, build);
    await writeFile(join(directory, file), bumped);
  }

  // 改完自查：漏改任一处都会留下不一致状态。
  const afterVersion = readVersion(await read('sw.js'));
  if (afterVersion !== updated) throw new Error(`sw.js 未更新到 ${updated}（仍为 ${afterVersion}）`);
  for (const { file, pattern } of TARGETS) {
    if (!(await read(file)).includes(updated)) throw new Error(`${file} 未同步到 ${updated}`);
  }
  if (commit) {
    const stamped = readCommit(await read('index.html'));
    if (stamped !== commit.slice(0, 7)) throw new Error(`提交标识未写入（仍为 ${stamped}）`);
  }
  if (!readCommit(await read('index.html'))) throw new Error('提交标识为空');
  console.log(`${current} -> ${updated}${commit ? ` @ ${commit.slice(0, 7)}` : ''}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}