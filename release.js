// 兜底版本号：仅在 downloads/latest.json 缺失或不可用时使用。
// 正常情况由 CI 在发版后写入 latest.json，这里无需改动。
const DEFAULT_TAG = 'v2.1.0';

const RELEASE_ORIGIN = 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/';

// 境内加速节点，按顺序探测，全部不可用时回退 GitHub 直链。
// 只收录回传 Access-Control-Allow-Origin 的节点，否则浏览器 HEAD 探测必然失败。
const MIRRORS = ['https://gh-proxy.org/', 'https://v4.gh-proxy.org/', 'https://gh-proxy.com/'];

// 由 tag 推导两个平台的附件名与 Release 直链，避免版本号散落多处。
function releaseFor(tag) {
  const version = tag.replace(/^v/, '');
  const assets = {};
  for (const [platform, suffix] of [['windows', 'win-x64-Setup.exe'], ['macos', 'osx-arm64.dmg']]) {
    const filename = `Intensive-Listening-${version}-${suffix}`;
    assets[platform] = { filename, source: `${RELEASE_ORIGIN}${tag}/${filename}` };
  }
  return { tag, assets };
}

function isReleaseAsset(source) {
  return typeof source === 'string' &&
    /^https:\/\/github\.com\/luyii-code-1\/Intensive_Listening\/releases\/download\/v2\.\d+\.\d+\/Intensive-Listening-\d+\.\d+\.\d+-(?:win-x64-Setup\.exe|osx-arm64\.dmg)$/.test(source);
}

// 只接受合法元数据：v2.x 稳定版，每项都带附件名与指向本仓库 Release 的来源。
// 校验不通过时返回空数组，调用方退回 DEFAULT_TAG 推导的地址。
function useMetadata(release) {
  if (typeof release?.tag !== 'string' || !/^v2\./.test(release.tag)) return null;
  const platforms = Object.keys(release.assets ?? {});
  const valid = platforms.length > 0 && platforms.every((platform) => {
    const asset = release.assets[platform];
    return asset && typeof asset.filename === 'string' &&
      (asset.size === undefined || (Number.isFinite(asset.size) && asset.size > 0)) &&
      isReleaseAsset(asset.source);
  });
  return valid ? release : null;
}

// 逐个探测加速节点：用 HEAD 确认节点真能取到该文件，避免把按钮指向死链。
async function pickMirror(sample) {
  for (const base of MIRRORS) {
    try {
      if ((await fetch(base + sample, { method: 'HEAD', cache: 'no-store' })).ok) return base;
    } catch {
      // 探测失败则尝试下一个节点。
    }
  }
  return null; // 所有加速节点都不可用时回退 GitHub 直链。
}

async function readMetadata() {
  try {
    const response = await fetch('downloads/latest.json', { cache: 'no-store' });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

async function switchDownloads() {
  const release = useMetadata(await readMetadata()) ?? releaseFor(DEFAULT_TAG);
  const platforms = Object.keys(release.assets);

  const base = await pickMirror(release.assets[platforms[0]].source);
  for (const platform of platforms) {
    const asset = release.assets[platform];
    const link = document.querySelector(`[data-download-platform="${platform}"]`);
    if (!link) continue;
    link.href = base === null ? asset.source : base + asset.source;
    link.download = asset.filename;
  }

  const label = document.getElementById('release-download-meta');
  if (label) label.textContent = `${release.tag} 正式版，${base === null ? '通过 GitHub 下载' : '境内加速下载'}`;
}

switchDownloads();