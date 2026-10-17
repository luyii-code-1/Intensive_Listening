// 发版后由 CI 调用：把 Release 元数据写进 downloads/latest.json，并同步 release.js 的兜底版本号。
// 用法：node scripts/publish-release.mjs <release.json> <site-root>
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const REPOSITORY = 'luyii-code-1/Intensive_Listening';
const PLATFORMS = { windows: 'win-x64-Setup.exe', macos: 'osx-arm64.dmg' };

export function buildMetadata(release) {
  const tag = release?.tag_name;
  if (release?.draft || release?.prerelease) throw new Error('Release is a draft or prerelease');
  const match = /^v(2\.\d+\.\d+)$/.exec(tag ?? '');
  if (!match) throw new Error(`Expected a stable 2.x tag, got ${JSON.stringify(tag)}`);
  const version = match[1];

  const assets = {};
  for (const [platform, suffix] of Object.entries(PLATFORMS)) {
    const filename = `Intensive-Listening-${version}-${suffix}`;
    const matches = (release.assets ?? []).filter((asset) => asset?.name === filename);
    if (matches.length !== 1) throw new Error(`Expected exactly one ${platform} asset named ${filename}`);
    const { size, digest, browser_download_url: source } = matches[0];
    if (!Number.isInteger(size) || size <= 0) throw new Error(`Missing size for ${filename}`);
    if (!/^sha256:[0-9a-f]{64}$/.test(digest ?? '')) throw new Error(`Missing SHA-256 for ${filename}`);
    const expected = `https://github.com/${REPOSITORY}/releases/download/${tag}/${filename}`;
    if (source !== expected) throw new Error(`Unexpected source URL for ${filename}: ${source}`);
    assets[platform] = { filename, path: `downloads/${filename}`, size, sha256: digest.slice(7), source };
  }
  return { tag, name: release.name ?? tag, assets };
}

export function syncFallbackTag(source, tag) {
  const pattern = /(DEFAULT_TAG\s*=\s*')[^']+(')/;
  if (!pattern.test(source)) throw new Error('DEFAULT_TAG not found in release.js');
  return source.replace(pattern, `$1${tag}$2`);
}

async function main() {
  const [releasePath, siteRoot] = process.argv.slice(2);
  if (!releasePath || !siteRoot) throw new Error('usage: publish-release.mjs <release.json> <site-root>');

  const metadata = buildMetadata(JSON.parse(await readFile(releasePath, 'utf8')));
  await mkdir(join(siteRoot, 'downloads'), { recursive: true });
  await writeFile(join(siteRoot, 'downloads', 'latest.json'), JSON.stringify(metadata, null, 2) + '\n');

  const releaseJs = join(siteRoot, 'release.js');
  const source = await readFile(releaseJs, 'utf8');
  const updated = syncFallbackTag(source, metadata.tag);
  if (updated !== source) await writeFile(releaseJs, updated);

  console.log(`Published ${metadata.tag}: ${Object.values(metadata.assets).map((a) => a.filename).join(', ')}`);
}

// pathToFileURL 处理 Windows 盘符与中文路径，直接比较字符串会在本地静默跳过。
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}