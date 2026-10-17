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
for (const file of ['index.html', 'guide.html', 'styles.css', 'release.js', 'assets']) {
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

for (const file of ['index.html', 'guide.html']) {
  const path = join(output, file);
  const page = await readFile(path, 'utf8');
  await writeFile(path, page.replaceAll('__VERSION__', tag.replace(/^v/, '')));
}
console.log(`Static site built in dist/ for ${tag}; EdgeOne routes are in root edge-functions/.`);