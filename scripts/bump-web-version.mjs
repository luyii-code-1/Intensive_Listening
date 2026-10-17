// 递增 Web 播放器（web-edition 分支）的构建号，并同步三处版本字面量。
//
// 版本形如 1.5.0+16；只动 +N 构建号，不动 1.5.0 主版本——
// 主版本由人决定（见 README「重新打一个版本」），CI 只负责把构建号推上去。
// sw.js 的 CACHE_NAME 必须同批改：它是让浏览器丢掉旧资源的唯一开关。
//
// 用法：node scripts/bump-web-version.mjs <webedition 目录>
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

// 必须同时更新的位置（index.html 有两处），缺一就会出现「显示 1.5.0+17 却仍在跑旧缓存」这类错配。
// 每条规则捕获 (前缀)(完整版本)(后缀)，只替换中间那段，避免吃掉引号或标签。
const VERSION = String.raw`\d+\.\d+\.\d+(?:\+\d+)?`;
const TARGETS = [
  { file: 'sw.js', pattern: new RegExp(String.raw`(CACHE_NAME\s*=\s*'il-pwa-)(${VERSION})(')`) },
  { file: 'index.html', pattern: new RegExp(String.raw`(<span class="version-text">)(${VERSION})(</span>)`) },
  { file: 'index.html', pattern: new RegExp(String.raw`(title="版本: )(${VERSION})( \(点击查看源码\)")`) },
  { file: 'js/app.js', pattern: new RegExp(String.raw`(app_version:\s*')(${VERSION})(')`) },
];

export function nextVersion(current) {
  const match = /^(\d+\.\d+\.\d+)(?:\+(\d+))?$/.exec(current);
  if (!match) throw new Error(`无法识别的版本号：${current}`);
  return `${match[1]}+${(match[2] ? Number(match[2]) : 0) + 1}`;
}

// 改写某个文件里所有适用位置；返回改写后的文本。
export function bumpSource(source, file, build) {
  const targets = TARGETS.filter((t) => t.file === file);
  if (targets.length === 0) throw new Error(`没有 ${file} 的版本改写规则`);
  return targets.reduce(
    (text, { pattern }) => text.replace(pattern, (_all, before, version, after) =>
      `${before}${nextVersion(version).replace(/\+.*$/, `+${build}`)}${after}`),
    source,
  );
}

export function readVersion(serviceWorkerSource) {
  const match = /CACHE_NAME\s*=\s*'il-pwa-(\d+\.\d+\.\d+(?:\+\d+)?)'/.exec(serviceWorkerSource);
  if (!match) throw new Error("sw.js 里找不到 CACHE_NAME = 'il-pwa-<版本>'");
  return match[1];
}

async function main() {
  const [directory] = process.argv.slice(2);
  if (!directory) throw new Error('usage: bump-web-version.mjs <webedition 目录>');

  const read = (file) => readFile(join(directory, file), 'utf8');
  const current = readVersion(await read('sw.js'));
  const updated = nextVersion(current);

  for (const file of [...new Set(TARGETS.map((t) => t.file))]) {
    const source = await read(file);
    await writeFile(join(directory, file), bumpSource(source, file, updated.split('+')[1]));
  }
  // 改完自查：避免某处规则没匹配上，留下版本不一致。
  const after = readVersion(await read('sw.js'));
  if (after !== updated) throw new Error(`sw.js 未更新到 ${updated}（仍为 ${after}）`);
  for (const { file, pattern } of TARGETS) {
    if (!pattern.test(await read(file)) || !(await read(file)).includes(updated)) {
      throw new Error(`${file} 未同步到 ${updated}`);
    }
  }
  console.log(`${current} -> ${updated}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}