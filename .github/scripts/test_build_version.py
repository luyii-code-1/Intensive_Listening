import concurrent.futures
from datetime import datetime
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('desktop_build_version', Path(__file__).parents[2] / 'scripts/build_version.py')
version = importlib.util.module_from_spec(spec)
spec.loader.exec_module(version)

class BuildVersionTests(unittest.TestCase):
    def test_three_channel_identities(self):
        release = version.identity('2.2.0', 'release', '20261021')
        insider = version.identity('2.2.0', 'insider', '20261021', 3)
        beta = version.identity('2.2.0', 'beta', '20261021')
        self.assertEqual('2.2.0', release['displayVersion'])
        self.assertEqual('2.2.0', release['informationalVersion'])
        self.assertEqual('2.2.0 Insider Preview 1021 3', insider['displayVersion'])
        self.assertEqual('2.2.0-insider.20261021.3', insider['informationalVersion'])
        self.assertEqual('2.2.0 Beta 1021', beta['displayVersion'])
        self.assertEqual('2.2.0-beta.20261021', beta['informationalVersion'])
        self.assertEqual('2.2.0.0', insider['fileVersion'])
        self.assertEqual('2.2.0-Insider-Preview-1021-3', insider['artifactVersion'])

    def test_four_part_base_and_invalid_date_rejected(self):
        for invalid in ('2.2.0.1', 'v2.2.0', '2.4'):
            with self.assertRaises(ValueError): version.identity(invalid, 'insider', '20261021')
        with self.assertRaises(ValueError): version.identity('2.2.0', 'beta', '20260230')

    def test_local_daily_sequences_are_unique_under_concurrency(self):
        with tempfile.TemporaryDirectory() as root:
            with concurrent.futures.ThreadPoolExecutor(6) as pool:
                values = list(pool.map(lambda _: version.local_sequence(Path(root), '20261021'), range(12)))
            self.assertEqual(list(range(1, 13)), sorted(values))
            self.assertEqual(1, version.local_sequence(Path(root), '20261022'))

    def test_ci_sequence_uses_china_run_date_and_excludes_beta_and_other_day(self):
        current = dict(id=40, workflow_id=1, created_at='2026-10-20T16:10:00Z', display_title='Desktop insider')
        records = [current, dict(current, id=39, created_at='2026-10-20T16:01:00Z'),
                   dict(current, id=38, display_title='Desktop beta'), dict(current, id=37, created_at='2026-10-20T15:59:59Z')]
        with patch.dict(version.os.environ, GITHUB_REPOSITORY='owner/repo', GITHUB_RUN_ID='40'):
            with patch.object(version, 'github', side_effect=[current, {'workflow_runs': records}]):
                self.assertEqual(('20261021', 2), version.ci_stamp())
            with patch.object(version, 'github', return_value=current):
                self.assertEqual(('20261021', 1), version.ci_stamp('release'))

    def test_prepare_and_reuse_preserve_build_identity(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            (root / 'Directory.Build.props').write_text('<Project><PropertyGroup><DesktopBaseVersion>2.2.0</DesktopBaseVersion></PropertyGroup></Project>')
            with patch.object(version, 'ROOT', root), patch('sys.argv', ['build_version.py']): version.main()
            initial = json.loads((root / 'artifacts/version/build.json').read_text())
            with patch.object(version, 'ROOT', root), patch('sys.argv', ['build_version.py', '--reuse']): version.main()
            self.assertEqual(initial, json.loads((root / 'artifacts/version/build.json').read_text()))
            self.assertEqual(1, len(list((root / 'artifacts/version/daily').glob('*.stamp'))))

    def test_stable_requires_exact_release_tag(self):
        with patch('sys.argv', ['build_version.py', '--channel', 'release', '--tag', 'v2.1.2.4']):
            with self.assertRaisesRegex(ValueError, 'Release tag'): version.main()

if __name__ == '__main__': unittest.main()
