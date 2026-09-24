import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';
import '../documents/docx_text_extractor.dart';
import '../ilp/lesson_exercises.dart';

enum CourseProjectStep { audio, transcription, review, completed }

enum ReviewPhase { grouping, cloze }

class CourseProject {
  const CourseProject({
    required this.id,
    required this.title,
    required this.createdAt,
    required this.updatedAt,
    required this.step,
    this.audioPath,
    this.audioDuration,
    this.transcriptionJobId,
    this.transcript = '',
    this.lastExportPath,
    required this.packageUuid,
    this.packageVersion = 0,
    this.reviewPhase = ReviewPhase.grouping,
    this.exercises = const LessonExercises(),
    this.automaticQuestionPlanApplied = false,
    this.autoQuestionPlanDeferred = false,
  });

  final String id;
  final String title;
  final DateTime createdAt;
  final DateTime updatedAt;
  final CourseProjectStep step;
  final String? audioPath;
  final Duration? audioDuration;
  final String? transcriptionJobId;
  final String transcript;
  final String? lastExportPath;
  final String packageUuid;
  final int packageVersion;
  final ReviewPhase reviewPhase;
  final LessonExercises exercises;
  final bool automaticQuestionPlanApplied;
  final bool autoQuestionPlanDeferred;

  bool get hasAudio => audioPath != null && audioPath!.isNotEmpty;
  bool get hasTranscript => transcript.trim().isNotEmpty;

  CourseProject copyWith({
    String? title,
    CourseProjectStep? step,
    Object? audioPath = _unset,
    Object? audioDuration = _unset,
    Object? transcriptionJobId = _unset,
    String? transcript,
    Object? lastExportPath = _unset,
    int? packageVersion,
    ReviewPhase? reviewPhase,
    LessonExercises? exercises,
    bool? automaticQuestionPlanApplied,
    bool? autoQuestionPlanDeferred,
    DateTime? updatedAt,
  }) {
    return CourseProject(
      id: id,
      title: title ?? this.title,
      createdAt: createdAt,
      updatedAt: updatedAt ?? DateTime.now(),
      step: step ?? this.step,
      audioPath: identical(audioPath, _unset)
          ? this.audioPath
          : audioPath as String?,
      audioDuration: identical(audioDuration, _unset)
          ? this.audioDuration
          : audioDuration as Duration?,
      transcriptionJobId: identical(transcriptionJobId, _unset)
          ? this.transcriptionJobId
          : transcriptionJobId as String?,
      transcript: transcript ?? this.transcript,
      lastExportPath: identical(lastExportPath, _unset)
          ? this.lastExportPath
          : lastExportPath as String?,
      packageUuid: packageUuid,
      packageVersion: packageVersion ?? this.packageVersion,
      reviewPhase: reviewPhase ?? this.reviewPhase,
      exercises: exercises ?? this.exercises,
      automaticQuestionPlanApplied:
          automaticQuestionPlanApplied ?? this.automaticQuestionPlanApplied,
      autoQuestionPlanDeferred:
          autoQuestionPlanDeferred ?? this.autoQuestionPlanDeferred,
    );
  }

  Map<String, dynamic> toJson() => {
    'version': 2,
    'id': id,
    'title': title,
    'createdAt': createdAt.toIso8601String(),
    'updatedAt': updatedAt.toIso8601String(),
    'step': step.name,
    'audioPath': audioPath,
    'audioDurationMs': audioDuration?.inMilliseconds,
    'transcriptionJobId': transcriptionJobId,
    'lastExportPath': lastExportPath,
    'packageUuid': packageUuid,
    'packageVersion': packageVersion,
    'reviewPhase': reviewPhase.name,
    'exercises': exercises.toJson(),
    'automaticQuestionPlanApplied': automaticQuestionPlanApplied,
    'autoQuestionPlanDeferred': autoQuestionPlanDeferred,
  };

  factory CourseProject.fromJson(
    Map<String, dynamic> json, {
    required String transcript,
  }) {
    final stepName = json['step'];
    return CourseProject(
      id: json['id'] as String,
      title: json['title'] as String,
      createdAt: DateTime.parse(json['createdAt'] as String),
      updatedAt: DateTime.parse(json['updatedAt'] as String),
      step: CourseProjectStep.values.firstWhere(
        (value) => value.name == stepName,
        orElse: () => CourseProjectStep.audio,
      ),
      audioPath: json['audioPath'] as String?,
      audioDuration: json['audioDurationMs'] is num
          ? Duration(milliseconds: (json['audioDurationMs'] as num).round())
          : null,
      transcriptionJobId: json['transcriptionJobId'] as String?,
      transcript: transcript,
      lastExportPath: json['lastExportPath'] as String?,
      packageUuid: json['packageUuid'] is String
          ? json['packageUuid'] as String
          : _stableUuid(json['id'] as String),
      packageVersion: json['packageVersion'] is num
          ? (json['packageVersion'] as num).round()
          : 0,
      reviewPhase: ReviewPhase.values.firstWhere(
        (value) => value.name == json['reviewPhase'],
        orElse: () => ReviewPhase.grouping,
      ),
      exercises: LessonExercises.fromJson(json['exercises']),
      automaticQuestionPlanApplied:
          json['automaticQuestionPlanApplied'] == true,
      autoQuestionPlanDeferred: json['autoQuestionPlanDeferred'] == true,
    );
  }
}

const _unset = Object();

@visibleForTesting
Directory? debugProjectsDirectory;

class CourseProjectStore {
  const CourseProjectStore();

  Future<Directory> rootDirectory() async {
    final override = debugProjectsDirectory;
    if (override != null) return override;
    final appData = await intensiveListeningDataDirectory();
    return Directory(p.join(appData.path, 'projects'));
  }

  Future<List<CourseProject>> loadAll() async {
    final root = await rootDirectory();
    if (!await root.exists()) return const [];
    return compute(_loadProjectsAtPath, root.path);
  }

  Future<CourseProject?> loadById(String id) async {
    final root = await rootDirectory();
    if (id.isEmpty || p.basename(id) != id) return null;
    return _loadDirectory(Directory(p.join(root.path, id)));
  }

  Future<CourseProject> create({File? audio}) async {
    final now = DateTime.now();
    final id = '${now.microsecondsSinceEpoch}';
    final title = audio == null
        ? '未命名项目'
        : p.basenameWithoutExtension(audio.path);
    var project = CourseProject(
      id: id,
      title: title,
      createdAt: now,
      updatedAt: now,
      step: CourseProjectStep.audio,
      packageUuid: _newUuid(),
    );
    await save(project);
    if (audio != null) project = await bindAudio(project, audio);
    return project;
  }

  Future<CourseProject> bindAudio(
    CourseProject project,
    File source, {
    Duration? duration,
  }) async {
    final directory = await _projectDirectory(project.id);
    await directory.create(recursive: true);
    final extension = p.extension(source.path).toLowerCase();
    final destination = File(p.join(directory.path, 'audio$extension'));
    if (p.normalize(source.path) != p.normalize(destination.path)) {
      await source.copy(destination.path);
    }
    final updated = project.copyWith(
      title: project.title == '未命名项目'
          ? p.basenameWithoutExtension(source.path)
          : project.title,
      audioPath: destination.path,
      audioDuration: duration,
      step: CourseProjectStep.transcription,
      transcriptionJobId: null,
      transcript: '',
      reviewPhase: ReviewPhase.grouping,
      exercises: const LessonExercises(),
      automaticQuestionPlanApplied: false,
      autoQuestionPlanDeferred: false,
    );
    await save(updated);
    return updated;
  }

  Future<CourseProjectExamDocument> importExamDocument(
    CourseProject project,
    File source, {
    DocxTextExtractor extractor = const DocxTextExtractor(),
  }) async {
    if (p.extension(source.path).toLowerCase() != '.docx') {
      throw const CourseProjectDocumentException('请选择 DOCX 试卷文件');
    }
    if (!await source.exists()) {
      throw const CourseProjectDocumentException('找不到指定 DOCX 文件');
    }

    final bytes = await source.readAsBytes();
    late final DocxTextResult extracted;
    try {
      extracted = await compute(extractor.extractBytes, bytes);
    } on DocxTextExtractionException catch (error) {
      throw CourseProjectDocumentException(error.message);
    }

    final directory = await _projectDirectory(project.id);
    await directory.create(recursive: true);
    final documentFile = File(p.join(directory.path, 'exam.docx'));
    final textFile = File(p.join(directory.path, 'exam.txt'));
    final metadataFile = File(p.join(directory.path, 'exam.json'));
    final importedAt = DateTime.now();
    final digest = sha256.convert(bytes).toString();
    await documentFile.writeAsBytes(bytes, flush: true);
    await textFile.writeAsString(extracted.text, flush: true);
    await metadataFile.writeAsString(
      const JsonEncoder.withIndent('  ').convert({
        'version': 1,
        'sourceName': p.basename(source.path),
        'sha256': digest,
        'importedAt': importedAt.toUtc().toIso8601String(),
        'paragraphCount': extracted.paragraphCount,
        'tableCount': extracted.tableCount,
      }),
      flush: true,
    );
    final updated = project.copyWith(updatedAt: importedAt);
    await save(updated);
    return CourseProjectExamDocument(
      project: updated,
      sourceName: p.basename(source.path),
      documentPath: documentFile.path,
      textPath: textFile.path,
      text: extracted.text,
      sha256: digest,
      paragraphCount: extracted.paragraphCount,
      tableCount: extracted.tableCount,
    );
  }

  Future<CourseProjectExamDocument?> readExamDocument(
    CourseProject project,
  ) async {
    final directory = await _projectDirectory(project.id);
    final documentFile = File(p.join(directory.path, 'exam.docx'));
    final textFile = File(p.join(directory.path, 'exam.txt'));
    if (!await documentFile.exists() || !await textFile.exists()) return null;

    var sourceName = 'exam.docx';
    var digest = '';
    var paragraphCount = 0;
    var tableCount = 0;
    final metadataFile = File(p.join(directory.path, 'exam.json'));
    if (await metadataFile.exists()) {
      try {
        final decoded = jsonDecode(await metadataFile.readAsString());
        if (decoded is Map<String, dynamic>) {
          if (decoded['sourceName'] is String) {
            sourceName = decoded['sourceName'] as String;
          }
          if (decoded['sha256'] is String) digest = decoded['sha256'] as String;
          if (decoded['paragraphCount'] is num) {
            paragraphCount = (decoded['paragraphCount'] as num).round();
          }
          if (decoded['tableCount'] is num) {
            tableCount = (decoded['tableCount'] as num).round();
          }
        }
      } catch (_) {
        // The extracted text remains usable when optional metadata is damaged.
      }
    }
    return CourseProjectExamDocument(
      project: project,
      sourceName: sourceName,
      documentPath: documentFile.path,
      textPath: textFile.path,
      text: await textFile.readAsString(),
      sha256: digest,
      paragraphCount: paragraphCount,
      tableCount: tableCount,
    );
  }

  Future<void> save(CourseProject project) async {
    final directory = await _projectDirectory(project.id);
    await directory.create(recursive: true);
    await File(p.join(directory.path, 'project.json')).writeAsString(
      const JsonEncoder.withIndent('  ').convert(project.toJson()),
      flush: true,
    );
    final transcriptFile = File(p.join(directory.path, 'transcript.srt'));
    if (project.transcript.trim().isNotEmpty) {
      await transcriptFile.writeAsString(project.transcript, flush: true);
    } else if (await transcriptFile.exists()) {
      await transcriptFile.writeAsString('', flush: true);
    }
  }

  Future<CourseProject?> _loadDirectory(Directory directory) async {
    try {
      final metadata = File(p.join(directory.path, 'project.json'));
      if (!await metadata.exists()) return null;
      final decoded = jsonDecode(await metadata.readAsString());
      if (decoded is! Map<String, dynamic> ||
          (decoded['version'] != 1 && decoded['version'] != 2)) {
        return null;
      }
      final transcriptFile = File(p.join(directory.path, 'transcript.srt'));
      final transcript = await transcriptFile.exists()
          ? await transcriptFile.readAsString()
          : '';
      return CourseProject.fromJson(decoded, transcript: transcript);
    } catch (_) {
      return null;
    }
  }

  Future<Directory> _projectDirectory(String id) async {
    final root = await rootDirectory();
    return Directory(p.join(root.path, id));
  }

  Future<void> delete(String id) async {
    final directory = await _projectDirectory(id);
    if (await directory.exists()) await directory.delete(recursive: true);
  }

  Future<Uint8List> exportZip(CourseProject project) async {
    final directory = await _projectDirectory(project.id);
    if (!await directory.exists()) {
      throw const CourseProjectArchiveException('工程目录不存在');
    }
    return compute(
      _exportProjectZip,
      _ProjectArchiveRequest(directory.path, project.toJson()),
    );
  }

  Future<File> exportZipToFile(CourseProject project, File output) async {
    final directory = await _projectDirectory(project.id);
    if (!await directory.exists()) {
      throw const CourseProjectArchiveException('工程目录不存在');
    }
    await compute(
      _writeProjectZip,
      _ProjectArchiveRequest(
        directory.path,
        project.toJson(),
        outputPath: output.path,
      ),
    );
    return output;
  }

  Future<CourseProject> importZip(File source) async {
    Archive archive;
    try {
      archive = await compute(
        _decodeProjectArchive,
        await source.readAsBytes(),
      );
    } catch (_) {
      throw const CourseProjectArchiveException('工程 ZIP 已损坏或格式无效');
    }
    final descriptor = archive.find('project-export.json')?.readBytes();
    final metadataBytes = archive.find('project/project.json')?.readBytes();
    if (descriptor == null || metadataBytes == null) {
      throw const CourseProjectArchiveException('ZIP 中缺少工程描述文件');
    }
    try {
      final decodedDescriptor = jsonDecode(utf8.decode(descriptor));
      if (decodedDescriptor is! Map<String, dynamic> ||
          decodedDescriptor['format'] != 'intensive-listening-project' ||
          decodedDescriptor['version'] != 1) {
        throw const CourseProjectArchiveException('工程 ZIP 版本不受支持');
      }
    } on CourseProjectArchiveException {
      rethrow;
    } catch (_) {
      throw const CourseProjectArchiveException('工程描述文件无效');
    }

    Map<String, dynamic> metadata;
    try {
      final decoded = jsonDecode(utf8.decode(metadataBytes));
      if (decoded is! Map<String, dynamic>) throw const FormatException();
      metadata = decoded;
    } catch (_) {
      throw const CourseProjectArchiveException('工程元数据无效');
    }

    final now = DateTime.now();
    final id = '${now.microsecondsSinceEpoch}';
    final destination = await _projectDirectory(id);
    await destination.create(recursive: true);
    try {
      for (final entry in archive) {
        if (!entry.isFile || entry.name == 'project/project.json') continue;
        if (!entry.name.startsWith('project/')) continue;
        final relative = entry.name.substring('project/'.length);
        final normalized = p.posix.normalize(relative);
        if (normalized.isEmpty ||
            normalized == '.' ||
            normalized.startsWith('../') ||
            p.posix.isAbsolute(normalized) ||
            entry.isSymbolicLink) {
          throw const CourseProjectArchiveException('工程 ZIP 包含不安全路径');
        }
        final bytes = entry.readBytes();
        if (bytes == null) continue;
        final output = File(
          p.joinAll([destination.path, ...p.posix.split(normalized)]),
        );
        await output.parent.create(recursive: true);
        await output.writeAsBytes(bytes, flush: true);
      }

      final portableAudioPath = metadata['audioPath'];
      String? audioPath;
      if (portableAudioPath is String && portableAudioPath.isNotEmpty) {
        final audio = File(
          p.join(destination.path, p.basename(portableAudioPath)),
        );
        if (!await audio.exists()) {
          throw const CourseProjectArchiveException('工程 ZIP 缺少绑定的音频文件');
        }
        audioPath = audio.path;
      }
      final transcriptFile = File(p.join(destination.path, 'transcript.srt'));
      final transcript = await transcriptFile.exists()
          ? await transcriptFile.readAsString()
          : '';
      metadata = {
        ...metadata,
        'version': 2,
        'id': id,
        'createdAt': now.toIso8601String(),
        'updatedAt': now.toIso8601String(),
        'audioPath': audioPath,
        'transcriptionJobId': null,
        'lastExportPath': null,
      };
      final project = CourseProject.fromJson(metadata, transcript: transcript);
      await save(project);
      return project;
    } on CourseProjectArchiveException {
      if (await destination.exists()) await destination.delete(recursive: true);
      rethrow;
    } catch (_) {
      if (await destination.exists()) await destination.delete(recursive: true);
      throw const CourseProjectArchiveException('工程 ZIP 无法完整导入');
    }
  }
}

class CourseProjectArchiveException implements Exception {
  const CourseProjectArchiveException(this.message);

  final String message;

  @override
  String toString() => message;
}

Future<List<CourseProject>> _loadProjectsAtPath(String rootPath) async {
  final projects = <CourseProject>[];
  await for (final entity in Directory(rootPath).list(followLinks: false)) {
    if (entity is! Directory) continue;
    final project = await const CourseProjectStore()._loadDirectory(entity);
    if (project != null) projects.add(project);
  }
  projects.sort((left, right) => right.updatedAt.compareTo(left.updatedAt));
  return projects;
}

class _ProjectArchiveRequest {
  const _ProjectArchiveRequest(
    this.directoryPath,
    this.metadata, {
    this.outputPath,
  });

  final String directoryPath;
  final Map<String, dynamic> metadata;
  final String? outputPath;
}

Future<void> _writeProjectZip(_ProjectArchiveRequest request) async {
  final bytes = await _exportProjectZip(request);
  await File(request.outputPath!).writeAsBytes(bytes, flush: true);
}

Future<Uint8List> _exportProjectZip(_ProjectArchiveRequest request) async {
  final directory = Directory(request.directoryPath);
  final archive = Archive();
  final metadata = <String, dynamic>{...request.metadata};
  if (metadata['audioPath'] is String) {
    metadata['audioPath'] = p.basename(metadata['audioPath'] as String);
  }
  archive.addFile(
    ArchiveFile.string(
      'project/project.json',
      const JsonEncoder.withIndent('  ').convert(metadata),
    ),
  );
  await for (final entity in directory.list(recursive: true)) {
    if (entity is! File) continue;
    final relative = p.relative(entity.path, from: directory.path);
    if (relative == 'project.json') continue;
    final archivePath = 'project/${p.posix.joinAll(p.split(relative))}';
    archive.addFile(ArchiveFile.bytes(archivePath, await entity.readAsBytes()));
  }
  archive.addFile(
    ArchiveFile.string(
      'project-export.json',
      jsonEncode({
        'format': 'intensive-listening-project',
        'version': 1,
        'exportedAt': DateTime.now().toUtc().toIso8601String(),
      }),
    ),
  );
  return ZipEncoder().encodeBytes(archive);
}

Archive _decodeProjectArchive(Uint8List bytes) =>
    ZipDecoder().decodeBytes(bytes, verify: true);

class CourseProjectExamDocument {
  const CourseProjectExamDocument({
    required this.project,
    required this.sourceName,
    required this.documentPath,
    required this.textPath,
    required this.text,
    required this.sha256,
    required this.paragraphCount,
    required this.tableCount,
  });

  final CourseProject project;
  final String sourceName;
  final String documentPath;
  final String textPath;
  final String text;
  final String sha256;
  final int paragraphCount;
  final int tableCount;
}

class CourseProjectDocumentException implements Exception {
  const CourseProjectDocumentException(this.message);

  final String message;

  @override
  String toString() => message;
}

String _newUuid() {
  final bytes = List<int>.generate(16, (_) => Random.secure().nextInt(256));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes
      .map((value) => value.toRadixString(16).padLeft(2, '0'))
      .join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-'
      '${hex.substring(20)}';
}

String _stableUuid(String seed) {
  final values = utf8.encode(seed);
  final bytes = List<int>.generate(
    16,
    (index) => values.isEmpty ? index : values[index % values.length] ^ index,
  );
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  final hex = bytes
      .map((value) => value.toRadixString(16).padLeft(2, '0'))
      .join();
  return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-'
      '${hex.substring(12, 16)}-${hex.substring(16, 20)}-'
      '${hex.substring(20)}';
}
