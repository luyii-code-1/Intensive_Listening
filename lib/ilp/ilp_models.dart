import 'dart:convert';

import 'lesson_exercises.dart';

class IlpManifest {
  const IlpManifest({
    required this.formatVersion,
    required this.title,
    required this.duration,
    required this.audioPath,
    required this.transcriptPath,
    required this.audioSha256,
    required this.transcriptSha256,
    required this.packageUuid,
    required this.packageVersion,
    this.exercises = const LessonExercises(),
  });

  static const supportedVersion = 2;
  static const supportedAudioExtensions = {'m4a', 'mp3', 'wav'};
  static const canonicalTranscriptPath = 'transcript.srt';

  final int formatVersion;
  final String title;
  final Duration duration;
  final String audioPath;
  final String transcriptPath;
  final String audioSha256;
  final String transcriptSha256;
  final String packageUuid;
  final int packageVersion;
  final LessonExercises exercises;

  List<int> toBytes() {
    final payload = {
      'formatVersion': formatVersion,
      'title': title,
      'durationMs': duration.inMilliseconds,
      'audioPath': audioPath,
      'transcriptPath': transcriptPath,
      'packageUuid': packageUuid,
      'packageVersion': packageVersion,
      'exercises': exercises.toJson(),
      'sha256': {audioPath: audioSha256, transcriptPath: transcriptSha256},
    };
    return utf8.encode(const JsonEncoder.withIndent('  ').convert(payload));
  }

  factory IlpManifest.fromBytes(List<int> bytes) {
    if (bytes.length > 256 * 1024) {
      throw const IlpException(IlpError.invalidManifest, 'manifest.json 过大');
    }

    late final Object? decoded;
    try {
      decoded = jsonDecode(utf8.decode(bytes));
    } on FormatException {
      throw const IlpException(
        IlpError.invalidManifest,
        'manifest.json 不是有效的 UTF-8 JSON',
      );
    }
    if (decoded is! Map<String, dynamic>) {
      throw const IlpException(
        IlpError.invalidManifest,
        'manifest.json 顶层必须是对象',
      );
    }

    final version = decoded['formatVersion'];
    if (version is! int) {
      throw const IlpException(IlpError.invalidManifest, 'formatVersion 必须是整数');
    }
    if (version != supportedVersion) {
      throw IlpException(
        IlpError.unsupportedVersion,
        '不支持的 .ilp 格式版本：$version',
      );
    }

    final title = decoded['title'];
    final durationMs = decoded['durationMs'];
    final audioPath = decoded['audioPath'];
    final transcriptPath = decoded['transcriptPath'];
    final hashes = decoded['sha256'];
    final packageUuid = decoded['packageUuid'];
    final packageVersion = decoded['packageVersion'];
    if (title is! String || title.trim().isEmpty || title.length > 300) {
      throw const IlpException(IlpError.invalidManifest, 'title 必须是非空字符串');
    }
    if (durationMs is! int || durationMs <= 0) {
      throw const IlpException(IlpError.invalidManifest, 'durationMs 必须是正整数');
    }
    if (!_isSupportedAudioPath(audioPath) ||
        transcriptPath != canonicalTranscriptPath) {
      throw const IlpException(
        IlpError.invalidPath,
        '精听包仅支持标准音频文件与 transcript.srt',
      );
    }
    if (hashes is! Map<String, dynamic>) {
      throw const IlpException(IlpError.invalidManifest, 'sha256 必须是对象');
    }
    if (packageUuid is! String || !_isUuid(packageUuid)) {
      throw const IlpException(
        IlpError.invalidManifest,
        'packageUuid 必须是 UUID',
      );
    }
    if (packageVersion is! int || packageVersion <= 0) {
      throw const IlpException(
        IlpError.invalidManifest,
        'packageVersion 必须是正整数',
      );
    }

    final audioHash = hashes[audioPath];
    final transcriptHash = hashes[transcriptPath];
    if (!_isSha256(audioHash) || !_isSha256(transcriptHash)) {
      throw const IlpException(
        IlpError.invalidManifest,
        'SHA-256 必须是 64 位小写十六进制字符串',
      );
    }

    return IlpManifest(
      formatVersion: version,
      title: title.trim(),
      duration: Duration(milliseconds: durationMs),
      audioPath: audioPath as String,
      transcriptPath: transcriptPath as String,
      audioSha256: audioHash as String,
      transcriptSha256: transcriptHash as String,
      packageUuid: packageUuid,
      packageVersion: packageVersion,
      exercises: LessonExercises.fromJson(decoded['exercises']),
    );
  }

  static bool _isSha256(Object? value) =>
      value is String && RegExp(r'^[a-f0-9]{64}$').hasMatch(value);

  static bool _isUuid(String value) => RegExp(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$',
    caseSensitive: false,
  ).hasMatch(value);

  static bool _isSupportedAudioPath(Object? value) {
    if (value is! String) return false;
    final match = RegExp(r'^audio\.([A-Za-z0-9]+)$').firstMatch(value);
    if (match == null) return false;
    return supportedAudioExtensions.contains(match.group(1)!.toLowerCase());
  }
}

class SrtCue {
  const SrtCue({required this.start, required this.end, required this.text});

  final Duration start;
  final Duration end;
  final String text;
}

class ImportedLesson {
  const ImportedLesson({
    required this.id,
    required this.directoryPath,
    required this.manifest,
    required this.cues,
  });

  final String id;
  final String directoryPath;
  final IlpManifest manifest;
  final List<SrtCue> cues;

  String get audioPath => '$directoryPath/${manifest.audioPath}';
  String get transcriptPath => '$directoryPath/${manifest.transcriptPath}';
}

enum IlpError {
  corruptArchive,
  missingFile,
  duplicateEntry,
  invalidManifest,
  unsupportedVersion,
  invalidPath,
  symbolicLink,
  hashMismatch,
  invalidTranscript,
  duplicatePackage,
}

class IlpException implements Exception {
  const IlpException(this.code, this.message);

  final IlpError code;
  final String message;

  @override
  String toString() => message;
}

String ilpPackageId(IlpManifest manifest) {
  return manifest.packageUuid;
}
