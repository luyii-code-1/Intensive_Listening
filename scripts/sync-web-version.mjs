// 同步 Web 播放器（web-edition 分支）版本到官网的 web.json。
//
// 版本的唯一来源是 web-edition 的 sw.js 缓存名：改它才会让浏览器丢掉旧缓存，
// 所以它与 index.html / js/app.js 里的显示值永远同批改动。
// 用法：node scripts/sync-web-version.mjs <webedition 目录或 sw.js 路径> [site-root]
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

// 形如 1.5.0+16 或 2.2.0+3；构建号可选。
export const VERSION_PATTERN = /\b(\d+\.\d+\.\d+(?:\+\d+)?)\b/;

export function parseWebVersion(serviceWorkerSource) {
  const cache = /CACHE_NAME\s*=\s*'il-pwa-([^']+)'/.exec(serviceWorkerSource);
  if (!cache) throw new Error("sw.js 里找不到 CACHE_NAME = 'il-pwa-<版本>'");
  const version = cache[1];
  if (!VERSION_PATTERN.test(version)) throw new Error(`无法识别的 Web 播放器版本：${version}`);
  return version;
}

export function renderWebJson(version) {
  return JSON.stringify({ webPlayer: version }, null, 2) + '\n';
}

async function main() {
  const [source, siteRoot = '.'] = process.argv.slice(2);
  if (!source) throw new Error('usage: sync-web-version.mjs <webedition 目录或 sw.js> [site-root]');

  const path = source.endsWith('.js') ? source : join(source, 'sw.js');
  const version = parseWebVersion(await readFile(path, 'utf8'));
  await writeFile(join(siteRoot, 'web.json'), renderWebJson(version));
  console.log(`Web 播放器版本已同步：${version}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}