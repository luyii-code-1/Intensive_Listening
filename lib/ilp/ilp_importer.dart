import 'dart:io';
import 'dart:isolate';

import 'package:archive/archive_io.dart';
import 'package:path/path.dart' as p;

import 'file_hashing.dart';
import 'ilp_models.dart';
import 'srt_parser.dart';

class IlpImporter {
  const IlpImporter(this.libraryDirectory);

  final Directory libraryDirectory;

  Future<ImportedLesson> importFile(
    File packageFile, {
    bool replaceExisting = false,
    String? titleOverride,
  }) async {
    return Isolate.run(
      () => _importFile(
        packageFile,
        replaceExisting: replaceExisting,
        titleOverride: titleOverride,
      ),
    );
  }

  Future<ImportedLesson> _importFile(
    File packageFile, {
    required bool replaceExisting,
    required String? titleOverride,
  }) async {
    if (!await _hasZipFileHeader(packageFile)) {
      throw const IlpException(IlpError.corruptArchive, '.ilp 文件不是有效的 ZIP');
    }
    await libraryDirectory.create(recursive: true);
    final stagingRoot = Directory(p.join(libraryDirectory.path, '.import'));
    await stagingRoot.create();
    final staging = await stagingRoot.createTemp('ilp-');

    InputFileStream? input;
    try {
      late final Archive archive;
      try {
        input = InputFileStream(packageFile.path);
        archive = ZipDecoder().decodeStream(input);
      } catch (_) {
        throw const IlpException(IlpError.corruptArchive, '.ilp 文件不是有效的 ZIP');
      }

      final entries = <String, ArchiveFile>{};
      for (final entry in archive) {
        if (entries.containsKey(entry.name)) {
          throw IlpException(
            IlpError.duplicateEntry,
            'ZIP 内存在重复条目：${entry.name}',
          );
        }
        entries[entry.name] = entry;
      }

      final manifestEntry = _requiredEntry(entries, 'manifest.json');
      final manifestBytes = manifestEntry.readBytes();
      if (manifestBytes == null) {
        throw const IlpException(
          IlpError.invalidManifest,
          '无法读取 manifest.json',
        );
      }
      var manifest = IlpManifest.fromBytes(manifestBytes);
      if (titleOverride != null && titleOverride.trim().isNotEmpty) {
        manifest = IlpManifest(
          formatVersion: manifest.formatVersion,
          title: titleOverride.trim(),
          duration: manifest.duration,
          audioPath: manifest.audioPath,
          transcriptPath: manifest.transcriptPath,
          audioSha256: manifest.audioSha256,
          transcriptSha256: manifest.transcriptSha256,
          packageUuid: manifest.packageUuid,
          packageVersion: manifest.packageVersion,
          exercises: manifest.exercises,
        );
      }
      final audioEntry = _requiredEntry(entries, manifest.audioPath);
      final transcriptEntry = _requiredEntry(entries, manifest.transcriptPath);

      final audioFile = File(p.join(staging.path, manifest.audioPath));
      final transcriptFile = File(
        p.join(staging.path, manifest.transcriptPath),
      );
      _extract(audioEntry, audioFile);
      _extract(transcriptEntry, transcriptFile);

      await verifyFileSha256(audioFile, manifest.audioSha256);
      await verifyFileSha256(transcriptFile, manifest.transcriptSha256);
      final cues = SrtParser.parse(
        await transcriptFile.readAsBytes(),
        manifest.duration,
      );

      await File(p.join(staging.path, 'manifest.json'))
          .writeAsBytes(manifest.toBytes(), flush: true);

      final id = ilpPackageId(manifest);
      final destination = Directory(p.join(libraryDirectory.path, id));
      if (await destination.exists()) {
        if (!replaceExisting) {
          throw const IlpException(IlpError.duplicatePackage, '该精听包已经导入');
        }
        await destination.delete(recursive: true);
      }
      final installed = await staging.rename(destination.path);
      return ImportedLesson(
        id: id,
        directoryPath: installed.path,
        manifest: manifest,
        cues: cues,
      );
    } on IlpException {
      rethrow;
    } catch (_) {
      throw const IlpException(IlpError.corruptArchive, '.ilp 导入失败');
    } finally {
      input?.closeSync();
      if (await staging.exists()) await staging.delete(recursive: true);
    }
  }

  Future<IlpManifest> readManifest(File packageFile) async {
    return Isolate.run(() => _readManifest(packageFile));
  }

  Future<IlpManifest> _readManifest(File packageFile) async {
    if (!await _hasZipFileHeader(packageFile)) {
      throw const IlpException(IlpError.corruptArchive, '.ilp 文件不是有效的 ZIP');
    }
    InputFileStream? input;
    try {
      late final Archive archive;
      try {
        input = InputFileStream(packageFile.path);
        archive = ZipDecoder().decodeStream(input);
      } catch (_) {
        throw const IlpException(IlpError.corruptArchive, '.ilp 文件不是有效的 ZIP');
      }

      final entries = <String, ArchiveFile>{
        for (final entry in archive) entry.name: entry,
      };
      final manifestBytes = _requiredEntry(
        entries,
        'manifest.json',
      ).readBytes();
      if (manifestBytes == null) {
        throw const IlpException(
          IlpError.invalidManifest,
          '无法读取 manifest.json',
        );
      }
      return IlpManifest.fromBytes(manifestBytes);
    } on IlpException {
      rethrow;
    } catch (_) {
      throw const IlpException(IlpError.corruptArchive, '.ilp 导入失败');
    } finally {
      input?.closeSync();
    }
  }

  Future<bool> _hasZipFileHeader(File file) async {
    try {
      final header = await file
          .openRead(0, 4)
          .expand((bytes) => bytes)
          .toList();
      return header.length == 4 &&
          header[0] == 0x50 &&
          header[1] == 0x4b &&
          header[2] == 0x03 &&
          header[3] == 0x04;
    } on FileSystemException {
      return false;
    }
  }

  ArchiveFile _requiredEntry(Map<String, ArchiveFile> entries, String name) {
    final entry = entries[name];
    if (entry == null || !entry.isFile) {
      throw IlpException(IlpError.missingFile, '精听包缺少 $name');
    }
    if (entry.isSymbolicLink) {
      throw IlpException(IlpError.symbolicLink, '$name 不能是符号链接');
    }
    return entry;
  }

  void _extract(ArchiveFile entry, File destination) {
    final output = OutputFileStream(destination.path);
    try {
      entry.writeContent(output);
    } finally {
      output.closeSync();
    }
  }
}
