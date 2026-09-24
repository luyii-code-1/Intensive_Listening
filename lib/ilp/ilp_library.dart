import 'dart:io';
import 'dart:isolate';

import 'package:path/path.dart' as p;

import 'file_hashing.dart';
import 'ilp_models.dart';
import 'srt_parser.dart';

class IlpLibrary {
  const IlpLibrary(this.directory);

  final Directory directory;

  Future<LibraryLoadResult> load() async {
    return Isolate.run(_load);
  }

  Future<LibraryLoadResult> _load() async {
    if (!await directory.exists()) {
      return const LibraryLoadResult(lessons: [], issues: []);
    }

    final lessons = <ImportedLesson>[];
    final issues = <LibraryEntryIssue>[];
    await for (final entity in directory.list(followLinks: false)) {
      if (entity is! Directory || p.basename(entity.path).startsWith('.')) {
        continue;
      }
      final id = p.basename(entity.path);
      try {
        lessons.add(await _loadLesson(id, entity, verifyAudio: false));
      } on IlpException catch (error) {
        issues.add(LibraryEntryIssue(id: id, message: error.message));
      } on FileSystemException {
        issues.add(LibraryEntryIssue(id: id, message: '课程文件无法读取'));
      }
    }

    lessons.sort(
      (left, right) => left.manifest.title.toLowerCase().compareTo(
        right.manifest.title.toLowerCase(),
      ),
    );
    return LibraryLoadResult(
      lessons: List.unmodifiable(lessons),
      issues: List.unmodifiable(issues),
    );
  }

  Future<List<LessonRef>> scanManifests() async {
    if (!await directory.exists()) return const [];

    final refs = <LessonRef>[];
    await for (final entity in directory.list(followLinks: false)) {
      if (entity is! Directory || p.basename(entity.path).startsWith('.')) {
        continue;
      }
      try {
        refs.add(
          LessonRef(
            id: p.basename(entity.path),
            directoryPath: entity.path,
            manifest: await _readManifest(entity),
          ),
        );
      } on IlpException {
        continue;
      } on FileSystemException {
        continue;
      }
    }
    refs.sort(
      (left, right) => left.manifest.title.toLowerCase().compareTo(
        right.manifest.title.toLowerCase(),
      ),
    );
    return List.unmodifiable(refs);
  }

  Future<LessonRef?> findByAudioSha256(String audioSha256) async {
    for (final ref in await scanManifests()) {
      if (ref.manifest.audioSha256 == audioSha256) return ref;
    }
    return null;
  }

  Future<LessonRef?> findByUuid(String packageUuid) async {
    for (final ref in await scanManifests()) {
      if (ref.manifest.packageUuid == packageUuid) return ref;
    }
    return null;
  }

  Future<LessonRef?> findByTitle(String title) async {
    final normalized = title.trim().toLowerCase();
    for (final ref in await scanManifests()) {
      if (ref.manifest.title.trim().toLowerCase() == normalized) return ref;
    }
    return null;
  }

  Future<void> remove(String id) async {
    final lessonDirectory = Directory(p.join(directory.path, id));
    if (await lessonDirectory.exists()) {
      await lessonDirectory.delete(recursive: true);
    }
  }

  Future<ImportedLesson?> loadById(String id) async {
    return Isolate.run(() => _loadById(id));
  }

  Future<ImportedLesson?> _loadById(String id) async {
    final lessonDirectory = Directory(p.join(directory.path, id));
    if (!await lessonDirectory.exists()) return null;
    try {
      return await _loadLesson(id, lessonDirectory, verifyAudio: true);
    } on IlpException {
      return null;
    } on FileSystemException {
      return null;
    }
  }

  Future<IlpManifest> _readManifest(Directory lessonDirectory) async {
    final manifestFile = File(p.join(lessonDirectory.path, 'manifest.json'));
    if (!await manifestFile.exists()) {
      throw const IlpException(IlpError.missingFile, '课程缺少 manifest.json');
    }
    return IlpManifest.fromBytes(await manifestFile.readAsBytes());
  }

  Future<ImportedLesson> _loadLesson(
    String id,
    Directory lessonDirectory, {
    required bool verifyAudio,
  }) async {
    final manifest = await _readManifest(lessonDirectory);
    final audioFile = File(p.join(lessonDirectory.path, manifest.audioPath));
    final transcriptFile = File(
      p.join(lessonDirectory.path, manifest.transcriptPath),
    );
    if (!await audioFile.exists() || !await transcriptFile.exists()) {
      throw const IlpException(IlpError.missingFile, '课程音频或字幕文件缺失');
    }

    if (verifyAudio) {
      await verifyFileSha256(audioFile, manifest.audioSha256);
    }
    await verifyFileSha256(transcriptFile, manifest.transcriptSha256);
    final cues = SrtParser.parse(
      await transcriptFile.readAsBytes(),
      manifest.duration,
    );
    return ImportedLesson(
      id: id,
      directoryPath: lessonDirectory.path,
      manifest: manifest,
      cues: cues,
    );
  }
}

class LibraryLoadResult {
  const LibraryLoadResult({required this.lessons, required this.issues});

  final List<ImportedLesson> lessons;
  final List<LibraryEntryIssue> issues;
}

class LibraryEntryIssue {
  const LibraryEntryIssue({required this.id, required this.message});

  final String id;
  final String message;
}

class LessonRef {
  const LessonRef({
    required this.id,
    required this.directoryPath,
    required this.manifest,
  });

  final String id;
  final String directoryPath;
  final IlpManifest manifest;
}
