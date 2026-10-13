"""Publish the complete development tree through a protected-main pull request."""

import json
import os
from pathlib import Path
import subprocess
import time

BRANCH = "automation/sync-2.0-dev-main"
TITLE = "chore(release): sync 2.0-dev snapshot to main"


def command(args, input=None):
    result = subprocess.run(args, input=input, text=True, encoding="utf-8", capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip() or f"Command failed: {args[0]}")
    return result.stdout.strip()


def git(*args, input=None):
    return command(["git", *args], input)


def snapshot(dev_sha, main_sha, git_command=git):
    tree = git_command("rev-parse", f"{dev_sha}^{{tree}}")
    message = f"{TITLE}\n\nSource 2.0-dev commit: {dev_sha}\n"
    commit = git_command("-c", "user.name=github-actions[bot]", "-c",
                         "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                         "commit-tree", tree, "-p", main_sha, "-p", dev_sha, input=message)
    if git_command("rev-parse", f"{commit}^{{tree}}") != tree or git_command("show", "-s", "--format=%P", commit) != f"{main_sha} {dev_sha}":
        raise RuntimeError("Snapshot tree or parents differ from the captured source.")
    return commit, tree


def check_state(pr, expected_head):
    if pr["headRefOid"] != expected_head:
        raise RuntimeError("Sync PR head changed during checks; rerun synchronization.")
    if pr["state"] != "OPEN":
        raise RuntimeError("Sync PR was closed before this run merged it; inspect the PR and rerun.")
    builds = [check for check in pr["statusCheckRollup"] if check.get("name") == "build"]
    for check in builds:
        if check.get("status") == "COMPLETED" and check.get("conclusion") != "SUCCESS":
            raise RuntimeError(f"Required build check failed: {check.get('conclusion')}. See {check.get('detailsUrl', '')}")
    return bool(builds) and all(check.get("conclusion") == "SUCCESS" for check in builds) and pr["mergeStateStatus"] == "CLEAN"


def assert_refs(expected_main, expected_dev, actual_main, actual_dev):
    if (actual_main, actual_dev) != (expected_main, expected_dev):
        raise RuntimeError("main or 2.0-dev changed during checks; rerun to capture a fresh snapshot.")


class Sync:
    def __init__(self, repository):
        self.repository = repository
        self.prefix = f"repos/{repository}"

    def api(self, path, body=None, method="POST"):
        args = ["gh", "api", f"{self.prefix}/{path}"]
        data = None
        if body is not None:
            args += ["--method", method, "--input", "-"]
            data = json.dumps(body)
        return json.loads(command(args, data))

    def ref(self, branch):
        return self.api(f"git/ref/heads/{branch}")["object"]["sha"]

    def fetch(self):
        git("fetch", "origin", "+refs/heads/main:refs/remotes/origin/main",
            "+refs/heads/2.0-dev:refs/remotes/origin/2.0-dev")
        return git("rev-parse", "origin/main"), git("rev-parse", "origin/2.0-dev")

    def open_pr(self):
        return json.loads(command(["gh", "pr", "list", "--repo", self.repository,
                                   "--base", "main", "--head", BRANCH, "--state", "open",
                                   "--json", "number,url"]))

    def align_development(self, published_main, captured_dev, tree, url=""):
        assert_refs(published_main, captured_dev, self.ref("main"), self.ref("2.0-dev"))
        if git("rev-parse", f"{published_main}^{{tree}}") != tree or git("merge-base", published_main, captured_dev) != captured_dev:
            raise RuntimeError("Published main does not contain the captured development tree and history.")
        git("push", "origin", f"{published_main}:refs/heads/2.0-dev")
        actual_main, actual_dev = self.fetch()
        assert_refs(published_main, published_main, actual_main, actual_dev)
        self.summary("Published and branches aligned", published_main, captured_dev, tree, url)
        print(f"main and 2.0-dev are aligned at {published_main}; development tree: {tree}")

    def run(self):
        main_sha, dev_sha = self.fetch()
        tree = git("rev-parse", f"{dev_sha}^{{tree}}")
        if main_sha == dev_sha:
            self.summary("Already up to date", main_sha, dev_sha, tree)
            print("main already matches the development tree.")
            return

        if git("rev-parse", f"{main_sha}^{{tree}}") == tree and git("merge-base", main_sha, dev_sha) == dev_sha:
            self.align_development(main_sha, dev_sha, tree)
            return

        commit, tree = snapshot(dev_sha, main_sha)
        refs = git("ls-remote", "--heads", "origin", f"refs/heads/{BRANCH}")
        previous = refs.split()[0] if refs else ""
        git("push", f"--force-with-lease=refs/heads/{BRANCH}:{previous}", "origin", f"{commit}:refs/heads/{BRANCH}")
        body = f"Publish the complete `2.0-dev` tree to `main` while preserving both branch histories.\n\nSource: `{dev_sha}`\nBase main: `{main_sha}`\nTree: `{tree}`\n\nThe sync workflow waits for the required `build` check, merges this snapshot, verifies the published tree, and fast-forwards `2.0-dev` to the published commit."
        existing = self.open_pr()
        if len(existing) > 1:
            raise RuntimeError("Multiple sync PRs found; inspect them before rerunning.")
        if existing:
            pr = self.api(f"pulls/{existing[0]['number']}", {"title": TITLE, "body": body}, "PATCH")
        else:
            pr = self.api("pulls", {"title": TITLE, "body": body, "head": BRANCH, "base": "main"})
        number = pr["number"]
        url = pr["html_url"]
        self.summary("Waiting for checks", main_sha, dev_sha, tree, url)
        print(f"Snapshot PR: {url}", flush=True)

        deadline = time.monotonic() + 1800
        while time.monotonic() < deadline:
            state = json.loads(command(["gh", "pr", "view", str(number), "--repo", self.repository,
                                        "--json", "headRefOid,state,mergeStateStatus,statusCheckRollup"]))
            if check_state(state, commit):
                break
            time.sleep(30)
        else:
            raise RuntimeError(f"Timed out waiting for build and merge eligibility: {url}")

        assert_refs(main_sha, dev_sha, self.ref("main"), self.ref("2.0-dev"))
        merged = self.api(f"pulls/{number}/merge", {"sha": commit, "merge_method": "merge"}, "PUT")
        if not merged.get("merged"):
            raise RuntimeError(f"GitHub did not merge the sync PR: {merged.get('message', '')}")
        published_main, current_dev = self.fetch()
        assert_refs(merged["sha"], dev_sha, published_main, current_dev)
        if git("rev-parse", f"{published_main}^{{tree}}") != tree or git("rev-parse", f"{current_dev}^{{tree}}") != tree:
            raise RuntimeError("Published main or current development tree differs from the captured snapshot.")
        self.align_development(published_main, dev_sha, tree, url)

    @staticmethod
    def summary(status, main_sha, dev_sha, tree, url=""):
        if path := os.environ.get("GITHUB_STEP_SUMMARY"):
            with Path(path).open("a", encoding="utf-8") as output:
                output.write(f"## Development snapshot synchronization\n\n- Status: {status}\n- Main: `{main_sha}`\n- Source development commit: `{dev_sha}`\n- Tree: `{tree}`\n")
                if url:
                    output.write(f"- Pull request: {url}\n")


if __name__ == "__main__":
    try:
        Sync(os.environ["GITHUB_REPOSITORY"]).run()
    except (RuntimeError, KeyError) as error:
        print(f"::error::{error}", flush=True)
        if path := os.environ.get("GITHUB_STEP_SUMMARY"):
            with Path(path).open("a", encoding="utf-8") as output:
                output.write("\n- Synchronization stopped; inspect the error and sync PR before rerunning.\n")
        raise SystemExit(1)
