import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/app_directories.dart';

void main() {
  late Directory tempDir;

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('standalone-dir-test-');
  });

  tearDown(() async {
    if (await tempDir.exists()) {
      await tempDir.delete(recursive: true);
    }
  });

  test('intensiveListeningRootDirectory returns default directory without standalone env', () async {
    final root = await intensiveListeningRootDirectory();
    expect(root.path, isNotEmpty);
  });

  test('intensiveListeningDataDirectory uses root directory on non-Windows', () async {
    if (!Platform.isWindows) {
      final root = await intensiveListeningRootDirectory();
      final data = await intensiveListeningDataDirectory();
      expect(data.path, equals(root.path));
    }
  });

  test('clearIntensiveListeningCache handles nonexistent cache folders safely', () async {
    final cleared = await clearIntensiveListeningCache();
    expect(cleared, isNonNegative);
  });
}
