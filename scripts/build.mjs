import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = join(root, 'dist');
if (dirname(output) !== root.replace(/[\\/]$/, '') || relative(root, output) !== 'dist') {
  throw new Error('Invalid site output directory');
}
await rm(output, { recursive: true, force: true });
await mkdir(output);
// downloads/ 必须进 dist：release.js 要按站点根路径取 downloads/latest.json。
// 注意 edge-functions 的路由是 /downloads/:tag/:file，两段的 latest.json 不会命中它。
for (const file of ['index.html', 'guide.html', 'styles.css', 'release.js', 'assets', 'downloads']) {
  await cp(join(root, file), join(output, file), { recursive: true });
}

// 版本号唯一来源：downloads/latest.json（由发版 CI 写入），其次是 release.js 里的 DEFAULT_TAG。
// 网页脚本、下载链接标签与应用内版本展示都取自这里，改版本只需改一处。
const readVersion = async (path, pattern) => {
  try {
    return (pattern.exec(await readFile(path, 'utf8')) ?? [])[1] ?? null;
  } catch {
    return null;
  }
};
const tag = await readVersion(join(root, 'downloads', 'latest.json'), /"tag"\s*:\s*"(v\d+\.\d+\.\d+)"/)
  ?? await readVersion(join(root, 'release.js'), /DEFAULT_TAG\s*=\s*'([^']+)'/);
if (!/^v2\.\d+\.\d+$/.test(tag ?? '')) throw new Error('Unable to resolve the site version');

// Web 播放器（web-edition 分支）版本，与桌面版是两条独立系列。
// 唯一来源是 web-edition 的 sw.js 缓存名，由 scripts/sync-web-version.mjs 写入 web.json。
// 取不到时退回占位符，让页面显示成「见线上」而不是一个可能错误的旧版本号。
const webVersion = await readVersion(join(root, 'web.json'), /"webPlayer"\s*:\s*"([^"]+)"/
  );
if (!webVersion) console.warn('web.json 缺失或不可用，Web 播放器版本显示为未知。');

for (const file of ['index.html', 'guide.html']) {
  const path = join(output, file);
  const page = await readFile(path, 'utf8');
  await writeFile(path, page
    .replaceAll('__VERSION__', tag.replace(/^v/, ''))
    .replaceAll('__WEB_VERSION__', webVersion ?? '未知'));
}
console.log(`Static site built in dist/ for ${tag}` +
  `${webVersion ? ` (Web 播放器 ${webVersion})` : ''}; EdgeOne routes are in root edge-functions/.`);