"""Resolve one desktop build identity shared by runtime and installers."""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

CHINA = timezone(timedelta(hours=8))
ROOT = Path(__file__).resolve().parents[1]


def identity(base, channel, day, sequence=1):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", base):
        raise ValueError("Desktop base version must contain exactly three numeric parts.")
    if channel not in ("release", "beta", "insider"):
        raise ValueError("Unknown desktop release channel.")
    if sequence < 1:
        raise ValueError("Daily build sequence must be positive.")
    date = datetime.strptime(day, "%Y%m%d").strftime("%m%d")
    suffix = "" if channel == "release" else f"-{channel}.{day}" + (f".{sequence}" if channel == "insider" else "")
    display = f"v{base}"
    if channel == "insider":
        display += f" Insider Preview {date} {sequence}"
    elif channel == "beta":
        display += f" Beta {date}"
    return dict(baseVersion=base, channel=channel, date=day, sequence=sequence,
                displayVersion=display, informationalVersion=base + suffix,
                fileVersion=base + ".0", artifactVersion=display.removeprefix("v").replace(" ", "-"))


def github(path):
    return json.loads(subprocess.check_output(["gh", "api", path], text=True, encoding="utf-8"))


def ci_stamp(channel="insider"):
    repo, run_id = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_RUN_ID"]
    current = github(f"repos/{repo}/actions/runs/{run_id}")
    stamp = datetime.fromisoformat(current["created_at"].replace("Z", "+00:00")).astimezone(CHINA)
    if channel != "insider":
        return stamp.strftime("%Y%m%d"), 1
    start = stamp.replace(hour=0, minute=0, second=0, microsecond=0).astimezone(timezone.utc)
    end = start + timedelta(days=1)
    query = start.strftime("%Y-%m-%dT%H:%M:%SZ") + ".." + end.strftime("%Y-%m-%dT%H:%M:%SZ")
    ids = set()
    page = 1
    while True:
        runs = github(f"repos/{repo}/actions/workflows/{current['workflow_id']}/runs?created={query}&per_page=100&page={page}")["workflow_runs"]
        ids.update(r["id"] for r in runs if r["id"] <= int(run_id) and r.get("display_title") == "Desktop insider" and
                   datetime.fromisoformat(r["created_at"].replace("Z", "+00:00")).astimezone(CHINA).date() == stamp.date())
        if len(runs) < 100:
            break
        page += 1
    if int(run_id) not in ids:
        raise ValueError("Cannot resolve the current run's daily build sequence.")
    return stamp.strftime("%Y%m%d"), len(ids)


def local_sequence(root, day):
    root.mkdir(parents=True, exist_ok=True)
    # O_EXCL reserves each number across concurrently launched local builds.
    for sequence in range(1, 10000):
        try:
            with (root / f"{day}-{sequence}.stamp").open("x", encoding="utf-8") as output:
                output.write(day)
            return sequence
        except FileExistsError:
            continue
    raise ValueError("Daily build sequence exhausted.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", choices=("release", "beta", "insider"), default="insider")
    parser.add_argument("--tag")
    parser.add_argument("--ci", action="store_true")
    parser.add_argument("--reuse", action="store_true")
    args = parser.parse_args()
    base = ET.parse(ROOT / "Directory.Build.props").findtext(".//DesktopBaseVersion")
    target = ROOT / "artifacts/version"
    if args.reuse and (target / "build.json").exists():
        existing = json.loads((target / "build.json").read_text(encoding="utf-8"))
        if existing["baseVersion"] != base:
            raise ValueError("Prepared build identity belongs to a different base version.")
        return
    day, sequence = ci_stamp(args.channel) if args.ci else (datetime.now(CHINA).strftime("%Y%m%d"), None)
    if args.channel == "release" and args.tag != f"v{base}":
        raise ValueError("Stable Release tag must equal v plus the desktop base version.")
    if args.channel == "beta" and args.tag and args.tag != f"v{base}-beta.{day}":
        raise ValueError("Pre-release tag must use vX.Y.Z-beta.YYYYMMDD.")
    sequence = sequence or (local_sequence(target / "daily", day) if args.channel == "insider" else 1)
    value = identity(base, args.channel, day, sequence)
    target.mkdir(parents=True, exist_ok=True)
    document = ET.Element("Project")
    properties = ET.SubElement(document, "PropertyGroup")
    for name, content in (("Version", value["baseVersion"]), ("AssemblyVersion", value["fileVersion"]),
                          ("FileVersion", value["fileVersion"]), ("InformationalVersion", value["informationalVersion"])):
        ET.SubElement(properties, name).text = content
    ET.indent(document)
    ET.ElementTree(document).write(target / "BuildVersion.props", encoding="utf-8", xml_declaration=True)
    (target / "build.json").write_text(json.dumps(value, indent=2), encoding="utf-8")
    print(value["displayVersion"])


if __name__ == "__main__":
    main()
