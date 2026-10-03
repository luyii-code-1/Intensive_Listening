#!/usr/bin/env python3
"""Match both build artifacts to published Release assets before deployment."""

import hashlib
import json
from pathlib import Path
import re
import sys


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
    if release.get("draft") or release.get("prerelease") or not re.fullmatch(r"v2\.[0-9]+\.[0-9]+", tag):
        raise ValueError("Expected a published stable 2.x Release")

    version = tag[1:]
    expected_names = {
        "windows": f"Intensive-Listening-{version}-win-x64-Setup.exe",
        "macos": f"Intensive-Listening-{version}-osx-arm64.dmg",
    }
    directories = {"windows": Path(sys.argv[2]), "macos": Path(sys.argv[3])}
    assets = {}
    for platform, name in expected_names.items():
        path = directories[platform] / name
        matches = [asset for asset in release["assets"] if asset["name"] == name]
        if not path.is_file() or len(matches) != 1:
            raise ValueError(f"Expected one build artifact and Release asset: {name}")
        asset = matches[0]
        size = path.stat().st_size
        digest = sha256(path)
        url = asset["browser_download_url"]
        if size <= 0 or size != asset["size"] or asset.get("digest") != f"sha256:{digest}":
            raise ValueError(f"Build artifact differs from published Release asset: {name}")
        if url != f"https://github.com/{REPO}/releases/download/{tag}/{name}":
            raise ValueError(f"Unexpected Release asset URL: {name}")
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
