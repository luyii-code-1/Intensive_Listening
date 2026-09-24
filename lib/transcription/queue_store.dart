import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import '../app_directories.dart';

const _queueFormatVersion = 1;

class StoredJob {
  const StoredJob({
    required this.id,
    required this.title,
    required this.audioPath,
    required this.status,
    required this.stage,
    required this.message,
    required this.enqueuedAt,
    this.projectId,
    this.startedAt,
    this.finishedAt,
    this.sha256,
    this.audioMd5,
    this.cacheProfile,
    this.audioDurationMs,
    this.srt,
    this.srtConsumed = false,
    this.duplicateApproved = false,
  });

  final String id;
  final String title;
  final String audioPath;
  final String status;
  final String stage;
  final String message;
  final DateTime enqueuedAt;
  final String? projectId;
  final DateTime? startedAt;
  final DateTime? finishedAt;
  final String? sha256;
  final String? audioMd5;
  final String? cacheProfile;
  final int? audioDurationMs;
  final String? srt;
  final bool srtConsumed;
  final bool duplicateApproved;

  Map<String, dynamic> toJson() {
    return {
      'id': id,
      'title': title,
      'audioPath': audioPath,
      'status': status,
      'stage': stage,
      'message': message,
      'enqueuedAt': enqueuedAt.toIso8601String(),
      'projectId': projectId,
      'startedAt': startedAt?.toIso8601String(),
      'finishedAt': finishedAt?.toIso8601String(),
      'sha256': sha256,
      'audioMd5': audioMd5,
      'cacheProfile': cacheProfile,
      'audioDurationMs': audioDurationMs,
      'srt': srt,
      'srtConsumed': srtConsumed,
      'duplicateApproved': duplicateApproved,
    };
  }

  factory StoredJob.fromJson(Map<String, dynamic> json) {
    return StoredJob(
      id: _stringValue(json['id'], ''),
      title: _stringValue(json['title'], ''),
      audioPath: _stringValue(json['audioPath'], ''),
      status: _stringValue(json['status'], 'queued'),
      stage: _stringValue(json['stage'], 'queued'),
      message: _stringValue(json['message'], ''),
      enqueuedAt: _dateValue(json['enqueuedAt']) ?? DateTime.now(),
      projectId: _nullableStringValue(json['projectId']),
      startedAt: _dateValue(json['startedAt']),
      finishedAt: _dateValue(json['finishedAt']),
      sha256: _nullableStringValue(json['sha256']),
      audioMd5: _nullableStringValue(json['audioMd5']),
      cacheProfile: _nullableStringValue(json['cacheProfile']),
      audioDurationMs: _nullableIntValue(json['audioDurationMs']),
      srt: _nullableStringValue(json['srt']),
      srtConsumed: json['srtConsumed'] == true,
      duplicateApproved: json['duplicateApproved'] == true,
    );
  }

  static String _stringValue(Object? value, String fallback) {
    return value is String ? value : fallback;
  }

  static String? _nullableStringValue(Object? value) {
    return value is String && value.isNotEmpty ? value : null;
  }

  static int? _nullableIntValue(Object? value) {
    if (value is int) return value;
    if (value is num) return value.round();
    return null;
  }

  static DateTime? _dateValue(Object? value) {
    return value is String ? DateTime.tryParse(value) : null;
  }
}

class QueueStore {
  QueueStore({Future<File> Function()? resolveFile})
    : _resolveFile = resolveFile ?? _defaultFile;

  static Future<File> _defaultFile() async {
    final directory = await intensiveListeningDataDirectory();
    return File(p.join(directory.path, 'transcription_queue.json'));
  }

  final Future<File> Function() _resolveFile;

  Future<List<StoredJob>> load() async {
    final file = await _resolveFile();
    if (!await file.exists()) return const [];
    try {
      final decoded = jsonDecode(await file.readAsString());
      if (decoded is! Map<String, dynamic>) return const [];
      if (decoded['version'] != _queueFormatVersion) return const [];
      final jobs = decoded['jobs'];
      if (jobs is! List) return const [];
      return jobs
          .whereType<Map<String, dynamic>>()
          .map(StoredJob.fromJson)
          .where((job) => job.id.isNotEmpty && job.audioPath.isNotEmpty)
          .toList(growable: false);
    } catch (_) {
      return const [];
    }
  }

  Future<void> save(List<StoredJob> jobs) async {
    final file = await _resolveFile();
    try {
      await file.parent.create(recursive: true);
      await file.writeAsString(
        const JsonEncoder.withIndent('  ').convert({
          'version': _queueFormatVersion,
          'jobs': [for (final job in jobs) job.toJson()],
        }),
        flush: true,
      );
    } catch (_) {}
  }
}
