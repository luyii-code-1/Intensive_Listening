#!/usr/bin/env python3
"""Activate an installer uploaded by the release workflow."""

import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import sys


DOWNLOADS = Path("/opt/1panel/www/sites/il.luyii.cn/index/downloads")
LATEST = "Intensive.Listening-Setup-latest.exe"
INSTALLER = re.compile(r"Intensive\.Listening-Setup-[0-9][A-Za-z0-9._-]*\.exe\Z")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    if len(sys.argv) != 3 or not all(part.isdecimal() for part in sys.argv[1:]):
        raise SystemExit("usage: deploy_installer.py RUN_ID RUN_ATTEMPT")

    suffix = "-".join(sys.argv[1:])
    incoming_installer = DOWNLOADS / f".incoming-{suffix}.exe"
    incoming_metadata = DOWNLOADS / f".incoming-{suffix}.json"
    with (DOWNLOADS / ".deploy.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        metadata = json.loads(incoming_metadata.read_text(encoding="utf-8"))
        name = metadata.get("asset")
        if not isinstance(name, str) or not INSTALLER.fullmatch(name):
            raise ValueError("Invalid installer asset name")
        if incoming_installer.stat().st_size != metadata.get("size"):
            raise ValueError("Installer size does not match Release")
        if sha256(incoming_installer) != metadata.get("sha256"):
            raise ValueError("Installer SHA-256 does not match Release")

        target = DOWNLOADS / name
        os.chmod(incoming_installer, 0o644)
        os.replace(incoming_installer, target)
        temporary_link = DOWNLOADS / f".{LATEST}.{suffix}"
        try:
            os.link(target, temporary_link)
            os.replace(temporary_link, DOWNLOADS / LATEST)
        finally:
            temporary_link.unlink(missing_ok=True)

        public_metadata = {key: metadata[key] for key in ("tag", "name", "size", "sha256", "source")}
        incoming_metadata.write_text(json.dumps(public_metadata, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
        os.chmod(incoming_metadata, 0o644)
        os.replace(incoming_metadata, DOWNLOADS / "latest.json")
        print(f"Deployed {metadata['tag']}: {name} ({metadata['sha256']})")


if __name__ == "__main__":
    main()
