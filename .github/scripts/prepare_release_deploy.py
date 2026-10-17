#!/usr/bin/env python3
"""Verify and prepare published Release installers for website deployment."""

import hashlib
import json
from pathlib import Path
import re
import sys
import tempfile
from urllib.request import Request, urlopen


REPO = "luyii-code-1/Intensive_Listening"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    if len(sys.argv) != 5:
        raise SystemExit("usage: prepare_release_deploy.py RELEASE_JSON WINDOWS_DIR MACOS_DIR OUTPUT_JSON")
    release = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    tag = release["tag_name"]
    match = re.fullmatch(r"v(2\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:-release)?", tag)
    if release.get("draft") or release.get("prerelease") or not match:
        raise ValueError("Expected a published stable 2.x Release")

    version = match.group(1)
    expected_names = {
        "windows": f"Intensive-Listening-{version}-win-x64-Setup.exe",
        "macos": f"Intensive-Listening-{version}-osx-arm64.dmg",
    }
    directories = {"windows": Path(sys.argv[2]), "macos": Path(sys.argv[3])}
    assets = {}
    for platform, name in expected_names.items():
        path = directories[platform] / name
        matches = [asset for asset in release["assets"] if asset["name"] == name]
        if len(matches) != 1:
            raise ValueError(f"Expected one Release asset: {name}")
        asset = matches[0]
        size = asset["size"]
        published_digest = asset.get("digest", "")
        if not isinstance(size, int) or size <= 0 or not re.fullmatch(r"sha256:[0-9a-f]{64}", published_digest):
            raise ValueError(f"Missing size or SHA-256 for {name}")
        digest = published_digest.split(":", 1)[1]
        url = asset["browser_download_url"]
        if url != f"https://github.com/{REPO}/releases/download/{tag}/{name}":
            raise ValueError(f"Unexpected Release asset URL: {name}")
        if not path.is_file() or path.stat().st_size != size or sha256(path) != digest:
            path.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix=".release-", dir=path.parent) as directory:
                downloaded = Path(directory) / name
                with urlopen(Request(url, headers={"User-Agent": "IL-release-deploy"}), timeout=180) as source, downloaded.open("wb") as target:
                    for block in iter(lambda: source.read(1024 * 1024), b""):
                        target.write(block)
                if downloaded.stat().st_size != size or sha256(downloaded) != digest:
                    raise ValueError(f"Size or SHA-256 mismatch for published installer: {name}")
                downloaded.replace(path)
        assets[platform] = {
            "filename": name,
            "path": f"downloads/{name}",
            "size": size,
            "sha256": digest,
            "source": url,
        }

    output = Path(sys.argv[4])
    output.write_text(json.dumps({"tag": tag, "name": release["name"], "assets": assets}, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"Verified both published installers for {tag}")


if __name__ == "__main__":
    main()
