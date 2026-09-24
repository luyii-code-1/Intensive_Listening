import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

const appVersion = String.fromEnvironment(
  'ILP_APP_VERSION',
  defaultValue: '1.0.0',
);
const appDataSchemaVersion = 1;

Future<Directory> intensiveListeningRootDirectory() async {
  if (Platform.isWindows) {
    final standaloneData = Platform.environment['ILP_STANDALONE_DATA']?.trim();
    if (standaloneData != null && standaloneData.isNotEmpty) {
      return Directory(standaloneData);
    }
    final appData =
        Platform.environment['LOCALAPPDATA'] ?? Platform.environment['APPDATA'];
    if (appData != null && appData.trim().isNotEmpty) {
      return Directory(p.join(appData, 'Intensive Listening'));
    }
  }

  if (Platform.isMacOS) {
    final userHome = Platform.environment['HOME'];
    if (userHome != null && userHome.trim().isNotEmpty) {
      return Directory(
        p.join(
          userHome,
          'Library',
          'Application Support',
          'Intensive Listening',
        ),
      );
    }
  }

  return Directory(p.join(Directory.current.path, '.intensive_listening'));
}

Future<Directory>? _windowsDataDirectory;

Future<Directory> intensiveListeningDataDirectory() async {
  if (!Platform.isWindows) return intensiveListeningRootDirectory();
  final standaloneData = Platform.environment['ILP_STANDALONE_DATA']?.trim();
  if (standaloneData != null && standaloneData.isNotEmpty) {
    final directory = Directory(standaloneData);
    await directory.create(recursive: true);
    return directory;
  }
  return _windowsDataDirectory ??= _prepareWindowsDataDirectory();
}

Future<Directory> intensiveListeningApplicationDataDirectory() async {
  final root = await intensiveListeningRootDirectory();
  final directory = Directory(p.join(root.path, 'application data'));
  await directory.create(recursive: true);
  return directory;
}

/// Removes only regenerable application files; projects and settings are kept.
Future<int> clearIntensiveListeningCache() async {
  final applicationData = await intensiveListeningApplicationDataDirectory();
  final data = await intensiveListeningDataDirectory();
  var bytes = 0;
  for (final directory in [
    Directory(p.join(applicationData.path, 'updates')),
    Directory(p.join(applicationData.path, 'cache')),
    Directory(p.join(data.path, 'cache')),
  ]) {
    if (!await directory.exists()) continue;
    await for (final entry in directory.list(
      recursive: true,
      followLinks: false,
    )) {
      if (entry is File) bytes += await entry.length();
    }
    await directory.delete(recursive: true);
  }
  return bytes;
}

Future<Directory> _prepareWindowsDataDirectory() async {
  final root = await intensiveListeningRootDirectory();
  final data = Directory(p.join(root.path, 'data'));
  final applicationData = await intensiveListeningApplicationDataDirectory();
  await data.create(recursive: true);

  // Versioned runtime folders stay at the root. Move only known user data;
  // an existing destination is never overwritten.
  for (final name in const [
    'settings.json',
    'lesson_progress.json',
    'transcription_queue.json',
    'library',
    'projects',
    'mcp',
  ]) {
    final source = p.join(root.path, name);
    final target = p.join(data.path, name);
    if (await File(target).exists() || await Directory(target).exists()) {
      continue;
    }
    if (await File(source).exists()) {
      await File(source).rename(target);
    } else if (await Directory(source).exists()) {
      await Directory(source).rename(target);
    }
  }

  await File(p.join(applicationData.path, 'version.json')).writeAsString(
    jsonEncode({
      'appVersion': Platform.environment['ILP_APP_VERSION'] ?? appVersion,
      'dataSchemaVersion': appDataSchemaVersion,
    }),
    flush: true,
  );
  return data;
}
