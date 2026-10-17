import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class ReleaseDeploymentTests(unittest.TestCase):
    def test_four_component_release_verifies_both_installers(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = b'verified release fixture'
            digest = hashlib.sha256(data).hexdigest()
            assets = []
            for suffix in ['win-x64-Setup.exe', 'osx-arm64.dmg']:
                name = f'Intensive-Listening-2.1.2.3-{suffix}'
                (root / name).write_bytes(data)
                assets.append({
                    'name': name, 'size': len(data), 'digest': 'sha256:' + digest,
                    'browser_download_url': 'https://github.com/luyii-code-1/Intensive_Listening/releases/download/v2.1.2.3/' + name,
                })
            release = {'tag_name': 'v2.1.2.3', 'name': 'Release fixture', 'assets': assets}
            metadata = root / 'release.json'
            metadata.write_text(json.dumps(release), encoding='utf-8')
            output = root / 'latest.json'
            script = Path(__file__).with_name('prepare_release_deploy.py')
            command = [sys.executable, str(script), str(metadata), str(root), str(root), str(output)]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            published = json.loads(output.read_text(encoding='utf-8'))
            self.assertEqual(published['tag'], 'v2.1.2.3')
            self.assertEqual(published['assets']['windows']['sha256'], digest)
            self.assertEqual(published['assets']['macos']['size'], len(data))

            release['tag_name'] = 'v2.1.2.3.4'
            metadata.write_text(json.dumps(release), encoding='utf-8')
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('stable 2.x Release', result.stderr)
