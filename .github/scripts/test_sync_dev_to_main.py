import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import sync_dev_to_main as sync


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="il-sync-test-")
        self.root = Path(self.temp.name)
        self.repo = self.root / "checkout"
        self.remote = self.root / "origin.git"
        subprocess.run(["git", "init", "--bare", str(self.remote)], check=True, capture_output=True)
        subprocess.run(["git", "init", "-b", "main", str(self.repo)], check=True, capture_output=True)
        self.git("config", "user.name", "Sync Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "core.autocrlf", "false")
        (self.repo / "old-main-only.txt").write_text("obsolete", encoding="utf-8")
        (self.repo / "README.md").write_text("old", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-m", "main base")
        self.main = self.git("rev-parse", "HEAD")
        self.git("switch", "-c", "2.0-dev")
        (self.repo / "old-main-only.txt").unlink()
        (self.repo / "README.md").write_text("development", encoding="utf-8")
        (self.repo / "binary.dat").write_bytes(b"\x00\xff\x80\x01")
        (self.repo / "run.sh").write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        self.git("add", "-A")
        self.git("update-index", "--chmod=+x", "run.sh")
        self.git("commit", "-m", "development tree")
        self.dev = self.git("rev-parse", "HEAD")
        self.git("remote", "add", "origin", str(self.remote))
        self.git("push", "origin", "main", "2.0-dev")
        self.calls = []
        self.failed = False
        self.existing = False
        self.source_moves = False
        self.publisher = FakeSync(self)

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args, input=None):
        result = subprocess.run(["git", *args], cwd=self.repo, input=input, text=True,
                                encoding="utf-8", capture_output=True, check=True)
        return result.stdout.strip()

    def remote_ref(self, branch):
        return self.git("ls-remote", "--heads", "origin", f"refs/heads/{branch}").split()[0]

    def command(self, args, input=None):
        if args[0] == "git":
            return self.git(*args[1:], input=input)
        if args[1:3] == ["pr", "list"]:
            return json.dumps([{"number": 7, "url": "https://example.invalid/pull/7"}] if self.existing else [])
        if args[1:3] == ["pr", "view"]:
            if self.source_moves:
                self.git("push", "--force", "origin", f"{self.main}:refs/heads/2.0-dev")
            return json.dumps({"headRefOid": self.remote_ref(sync.BRANCH), "state": "OPEN",
                               "mergeStateStatus": "CLEAN", "statusCheckRollup": [
                                   {"name": "build", "status": "COMPLETED",
                                    "conclusion": "FAILURE" if self.failed else "SUCCESS"}]})
        self.fail(f"Unexpected command: {args}")

    def run_sync(self):
        with patch.object(sync, "command", self.command), patch.dict(sync.os.environ, {"GITHUB_STEP_SUMMARY": ""}):
            self.publisher.run()

    def test_snapshot_replaces_main_tree_and_preserves_binary_and_executable_mode(self):
        commit, tree = sync.snapshot(self.dev, self.main, self.git)
        self.assertEqual(self.git("rev-parse", f"{self.dev}^{{tree}}"), tree)
        self.assertEqual(self.main, self.git("show", "-s", "--format=%P", commit))
        self.assertEqual("100755", self.git("ls-tree", commit, "run.sh").split()[0])
        self.assertEqual("", self.git("ls-tree", commit, "old-main-only.txt"))

    def test_full_sync_creates_pr_merges_and_verifies_native_git_tree(self):
        self.run_sync()
        self.assertIn(("pulls", "POST"), self.calls)
        self.assertIn(("pulls/7/merge", "PUT"), self.calls)
        self.assertEqual(self.git("rev-parse", f"{self.dev}^{{tree}}"), self.git("rev-parse", "origin/main^{tree}"))

    def test_reuses_existing_sync_branch_and_pr(self):
        self.git("push", "origin", f"{self.main}:refs/heads/{sync.BRANCH}")
        self.existing = True
        self.run_sync()
        self.assertIn(("pulls/7", "PATCH"), self.calls)
        self.assertNotIn(("pulls", "POST"), self.calls)

    def test_failed_build_keeps_main_unchanged(self):
        self.failed = True
        with self.assertRaisesRegex(RuntimeError, "build check failed"):
            self.run_sync()
        self.assertEqual(self.main, self.remote_ref("main"))
        self.assertNotIn(("pulls/7/merge", "PUT"), self.calls)

    def test_source_moving_during_checks_stops_before_merge(self):
        self.source_moves = True
        with self.assertRaisesRegex(RuntimeError, "changed during checks"):
            self.run_sync()
        self.assertEqual(self.main, self.remote_ref("main"))
        self.assertNotIn(("pulls/7/merge", "PUT"), self.calls)

    def test_identical_trees_are_noop(self):
        self.git("push", "origin", f"{self.dev}:refs/heads/main")
        self.run_sync()
        self.assertEqual([], self.calls)

    def test_wait_timeout_does_not_merge(self):
        with patch.object(sync.time, "monotonic", side_effect=[0, 1801]):
            with self.assertRaisesRegex(RuntimeError, "Timed out"):
                self.run_sync()
        self.assertEqual(self.main, self.remote_ref("main"))

    def test_moved_pr_head_and_blocked_review_cannot_merge(self):
        state = {"headRefOid": "expected", "state": "OPEN", "mergeStateStatus": "BLOCKED",
                 "statusCheckRollup": [{"name": "build", "status": "COMPLETED", "conclusion": "SUCCESS"}]}
        self.assertFalse(sync.check_state(state, "expected"))
        state["headRefOid"] = "other"
        with self.assertRaisesRegex(RuntimeError, "head changed"):
            sync.check_state(state, "expected")


class FakeSync(sync.Sync):
    def __init__(self, fixture):
        super().__init__("test/repository")
        self.fixture = fixture

    def api(self, path, body=None, method="POST"):
        fixture = self.fixture
        if path.startswith("git/ref/heads/"):
            return {"object": {"sha": fixture.remote_ref(path[len("git/ref/heads/"):])}}
        fixture.calls.append((path, method))
        if path == "pulls/7/merge":
            head = fixture.remote_ref(sync.BRANCH)
            assert body["sha"] == head
            tree = fixture.git("rev-parse", f"{head}^{{tree}}")
            merged = fixture.git("commit-tree", tree, "-p", fixture.remote_ref("main"), "-p", head, input="Merge sync PR\n")
            fixture.git("push", "origin", f"{merged}:refs/heads/main")
            return {"merged": True, "sha": merged}
        return {"number": 7, "html_url": "https://example.invalid/pull/7"}


if __name__ == "__main__":
    unittest.main()
