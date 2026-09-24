import 'dart:convert';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:archive/archive_io.dart';
import 'package:path/path.dart' as p;

const standaloneLessonLauncherRelativePath =
    'data/tools/lesson_player_launcher.exe';
const _standaloneFooterMagic = 'ILPPLAYERPACKV1!';
const _standaloneFooterLength = 80;

class StandaloneLessonExportException implements Exception {
  const StandaloneLessonExportException(this.message);

  final String message;

  @override
  String toString() => message;
}

class StandaloneLessonExporter {
  const StandaloneLessonExporter();

  Future<File> create({
    required File ilpFile,
    required File outputFile,
    required String packageUuid,
    required int packageVersion,
    required String title,
  }) async {
    return Isolate.run(
      () => _createStandalone(
        ilpFile: ilpFile,
        outputFile: outputFile,
        packageUuid: packageUuid,
        packageVersion: packageVersion,
        title: title,
      ),
    );
  }

  Future<File> _createStandalone({
    required File ilpFile,
    required File outputFile,
    required String packageUuid,
    required int packageVersion,
    required String title,
  }) async {
    if (!Platform.isWindows) {
      throw const StandaloneLessonExportException('独立播放器需要在 Windows 版本中生成');
    }
    final runtimeDirectory = Directory(p.dirname(Platform.resolvedExecutable));
    final outputPath = p.normalize(outputFile.absolute.path);
    final launcher = File(
      p.joinAll([
        runtimeDirectory.path,
        ...p.posix.split(standaloneLessonLauncherRelativePath),
      ]),
    );
    if (!await launcher.exists()) {
      throw const StandaloneLessonExportException('当前安装缺少独立播放器组件');
    }
    if (!await ilpFile.exists()) {
      throw const StandaloneLessonExportException('精听包尚未生成');
    }

    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-standalone-',
    );
    try {
      final metadata = File(p.join(temporaryDirectory.path, 'lesson.json'));
      await metadata.writeAsString(
        jsonEncode({
          'format': 'intensive-listening-standalone-lesson',
          'version': 1,
          'title': title,
          'packageUuid': packageUuid,
          'packageVersion': packageVersion,
        }),
        flush: true,
      );
      final payload = File(p.join(temporaryDirectory.path, 'payload.zip'));
      final encoder = ZipFileEncoder();
      encoder.create(payload.path, level: ZipFileEncoder.gzip);
      await encoder.addDirectory(
        runtimeDirectory,
        includeDirName: false,
        level: ZipFileEncoder.gzip,
        followLinks: false,
        filter: (entity, _) {
          if (p.equals(p.normalize(entity.absolute.path), outputPath)) {
            return ZipFileOperation.skip;
          }
          final relative = p
              .relative(entity.path, from: runtimeDirectory.path)
              .replaceAll('\\', '/');
          return relative == standaloneLessonLauncherRelativePath
              ? ZipFileOperation.skip
              : ZipFileOperation.include;
        },
      );
      await encoder.addFile(ilpFile, 'lesson.ilp', ZipFileEncoder.gzip);
      await encoder.addFile(metadata, 'lesson.json', ZipFileEncoder.gzip);
      await encoder.close();

      await outputFile.parent.create(recursive: true);
      final output = outputFile.openWrite(mode: FileMode.write);
      final launcherLength = await launcher.length();
      final payloadLength = await payload.length();
      await output.addStream(launcher.openRead());
      await output.addStream(payload.openRead());
      output.add(
        _footer(
          payloadOffset: launcherLength,
          payloadLength: payloadLength,
          packageId: '${packageUuid}_v$packageVersion',
        ),
      );
      await output.close();
      return outputFile;
    } finally {
      if (await temporaryDirectory.exists()) {
        await temporaryDirectory.delete(recursive: true);
      }
    }
  }

  Uint8List _footer({
    required int payloadOffset,
    required int payloadLength,
    required String packageId,
  }) {
    final magic = ascii.encode(_standaloneFooterMagic);
    if (magic.length != 16) throw StateError('Invalid standalone footer');
    final identifier = ascii.encode(packageId);
    if (identifier.length > 47) {
      throw const StandaloneLessonExportException('课程标识过长');
    }
    final bytes = Uint8List(_standaloneFooterLength);
    bytes.setRange(0, magic.length, magic);
    final data = ByteData.sublistView(bytes);
    data.setUint64(16, payloadOffset, Endian.little);
    data.setUint64(24, payloadLength, Endian.little);
    bytes.setRange(32, 32 + identifier.length, identifier);
    return bytes;
  }
}
