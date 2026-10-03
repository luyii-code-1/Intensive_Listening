#!/usr/bin/env python3
"""Verify and activate a two-platform Release uploaded by GitHub Actions."""

import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import sys


DOWNLOADS = Path("/opt/1panel/www/sites/il.luyii.cn/index/downloads")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    if len(sys.argv) != 3 or not all(part.isdecimal() for part in sys.argv[1:]):
        raise SystemExit("usage: deploy_release.py RUN_ID RUN_ATTEMPT")
    suffix = "-".join(sys.argv[1:])
    incoming_metadata = DOWNLOADS / f".incoming-{suffix}.json"

    with (DOWNLOADS / ".deploy.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        metadata = json.loads(incoming_metadata.read_text(encoding="utf-8"))
        tag = metadata.get("tag")
        if not isinstance(tag, str) or not re.fullmatch(r"v2\.[0-9]+\.[0-9]+", tag):
            raise ValueError("Invalid Release tag")
        version = tag[1:]
        expected = {
            "windows": (f"Intensive-Listening-{version}-win-x64-Setup.exe", ".exe"),
            "macos": (f"Intensive-Listening-{version}-osx-arm64.dmg", ".dmg"),
        }
        incoming = {}
        for platform, (name, extension) in expected.items():
            asset = metadata["assets"][platform]
            source = DOWNLOADS / f".incoming-{suffix}-{platform}{extension}"
            if asset["filename"] != name or asset["path"] != f"downloads/{name}":
                raise ValueError(f"Invalid {platform} installer metadata")
            if source.stat().st_size != asset["size"] or sha256(source) != asset["sha256"]:
                raise ValueError(f"Size or SHA-256 mismatch for {name}")
            incoming[platform] = source

        for platform, (name, _) in expected.items():
            os.chmod(incoming[platform], 0o644)
            os.replace(incoming[platform], DOWNLOADS / name)
        os.chmod(incoming_metadata, 0o644)
        os.replace(incoming_metadata, DOWNLOADS / "latest.json")
        print(f"Deployed Windows and macOS installers for {tag}")


if __name__ == "__main__":
    main()
