fetch('downloads/latest.json', { cache: 'no-store' })
  .then((response) => {
    if (!response.ok) throw new Error('Release metadata unavailable');
    return response.json();
  })
  .then((release) => {
    if (typeof release.tag !== 'string' || !/^v2\./.test(release.tag)) return;
    const platforms = ['windows', 'macos'];
    if (!platforms.every((platform) => {
      const asset = release.assets?.[platform];
      return asset && typeof asset.filename === 'string' &&
        asset.path === `downloads/${asset.filename}` &&
        Number.isFinite(asset.size) && asset.size > 0;
    })) return;

    for (const platform of platforms) {
      const asset = release.assets[platform];
      const link = document.querySelector(`[data-download-platform="${platform}"]`);
      if (!link) continue;
      link.href = asset.path;
      link.download = asset.filename;
    }
    const label = document.getElementById('release-download-meta');
    if (label) label.textContent = `${release.tag}，Windows 与 macOS 本站镜像`;
  })
  .catch(() => {});
