import unittest
from desktop_version import valid_version


class DesktopVersionTests(unittest.TestCase):
    def test_supported_identities(self):
        for value in ('2.1.2', '2.1.2.4', 'v2.1.2', 'v2.1.2 Insider Preview 1022 1',
                      '2.1.2-insider.20261022.2', 'v2.1.2 Beta 1022', '2.1.2-beta.20261022'):
            with self.subTest(value=value):
                self.assertTrue(valid_version(value))

    def test_invalid_identities(self):
        for value in ('2.1', 'v2.1.2 Insider Preview 1322 1', '2.1.2-insider.20261022',
                      'v2.1.2 Insider Preview 1022 0', '2.1.2-beta.20261022.1', '2.1.2\n'):
            with self.subTest(value=value):
                self.assertFalse(valid_version(value))
