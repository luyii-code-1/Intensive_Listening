import 'dart:convert';
import 'dart:io';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter/services.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';

class AppUpdateException implements Exception {
  const AppUpdateException(this.message);

  final String message;

  @override
  String toString() => message;
}

class StagedAppUpdate {
  const StagedAppUpdate({required this.version, required this.executable});

  final String version;
  final File executable;
}

class AppUpdateService {
  const AppUpdateService();

  static const executableName = 'Intensive Listening-Setup.exe';
  static const manifestName = 'update.json';
  static const _installerChannel = MethodChannel('intensive_listening/installer');

  String get currentVersion =>
      Platform.environment['ILP_APP_VERSION'] ?? appVersion;

  Future<StagedAppUpdate> stage(File zip) async {
    if (!Platform.isWindows) {
      throw const AppUpdateException('应用更新仅支持 Windows。');
    }
    if (!await zip.exists()) {
      throw const AppUpdateException('更新包不存在。');
    }

    final input = InputFileStream(zip.path);
    Directory? staging;
    var complete = false;
    try {
      final archive = ZipDecoder().decodeStream(input);
      final files = archive.where((entry) => entry.isFile).toList();
      if (files.length != 2 ||
          files.any(
            (entry) =>
                entry.name != manifestName && entry.name != executableName,
          )) {
        throw const AppUpdateException('更新包应只包含 update.json 和安装程序。');
      }
      final manifestEntry = archive.find(manifestName);
      final executableEntry = archive.find(executableName);
      if (manifestEntry == null ||
          executableEntry == null ||
          manifestEntry.size > 16 * 1024 ||
          executableEntry.size < 1024 * 1024 ||
          executableEntry.size > 1024 * 1024 * 1024) {
        throw const AppUpdateException('更新包内容无效。');
      }

      final manifest = jsonDecode(utf8.decode(manifestEntry.content));
      if (manifest is! Map<String, dynamic> ||
          manifest['format'] != 'intensive-listening-installer-update' ||
          manifest['schemaVersion'] != 1 ||
          manifest['fileName'] != executableName) {
        throw const AppUpdateException('更新包版本信息无效。');
      }
      final version = manifest['appVersion'];
      final expectedHash = manifest['sha256'];
      if (version is! String ||
          _compareVersions(version, currentVersion) <= 0 ||
          expectedHash is! String ||
          !RegExp(r'^[0-9a-f]{64}$').hasMatch(expectedHash)) {
        throw const AppUpdateException('更新版本须高于当前版本，且包含有效校验值。');
      }

      final appData = await intensiveListeningApplicationDataDirectory();
      final updates = Directory(p.join(appData.path, 'updates'));
      await updates.create(recursive: true);
      staging = await updates.createTemp('$version-');
      final executable = File(p.join(staging.path, executableName));
      final output = OutputFileStream(executable.path);
      try {
        executableEntry.writeContent(output);
      } finally {
        output.closeSync();
      }
      final actualHash = (await sha256.bind(executable.openRead()).first)
          .toString();
      if (actualHash != expectedHash) {
        throw const AppUpdateException('更新包校验失败。');
      }
      complete = true;
      return StagedAppUpdate(version: version, executable: executable);
    } on AppUpdateException {
      rethrow;
    } catch (error) {
      throw AppUpdateException('无法读取更新包：$error');
    } finally {
      await input.close();
      if (staging != null && !complete && await staging.exists()) {
        await staging.delete(recursive: true);
      }
    }
  }

  Future<void> install(StagedAppUpdate update) async {
    try {
      await _installerChannel.invokeMethod<void>(
        'launchInstaller',
        update.executable.path,
      );
    } on PlatformException catch (error) {
      throw AppUpdateException('无法启动安装程序：${error.message ?? error.code}');
    }
  }
}

int _compareVersions(String incoming, String installed) {
  final pattern = RegExp(r'^\d+\.\d+\.\d+$');
  if (!pattern.hasMatch(incoming) || !pattern.hasMatch(installed)) return -1;
  final left = incoming.split('.').map(int.parse).toList();
  final right = installed.split('.').map(int.parse).toList();
  for (var index = 0; index < 3; index++) {
    final difference = left[index].compareTo(right[index]);
    if (difference != 0) return difference;
  }
  return 0;
}
