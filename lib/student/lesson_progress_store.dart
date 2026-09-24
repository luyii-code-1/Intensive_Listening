import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';

@visibleForTesting
File? debugLessonProgressFile;

class LessonProgress {
  const LessonProgress({
    required this.packageUuid,
    this.position = Duration.zero,
    this.lastOpenedAt,
    this.revealedCloze = const {},
  });

  final String packageUuid;
  final Duration position;
  final DateTime? lastOpenedAt;
  final Set<String> revealedCloze;

  LessonProgress copyWith({
    Duration? position,
    DateTime? lastOpenedAt,
    Set<String>? revealedCloze,
  }) {
    return LessonProgress(
      packageUuid: packageUuid,
      position: position ?? this.position,
      lastOpenedAt: lastOpenedAt ?? this.lastOpenedAt,
      revealedCloze: revealedCloze ?? this.revealedCloze,
    );
  }

  Map<String, dynamic> toJson() => {
    'packageUuid': packageUuid,
    'positionMs': position.inMilliseconds,
    'lastOpenedAt': lastOpenedAt?.toIso8601String(),
    'revealedCloze': revealedCloze.toList()..sort(),
  };

  factory LessonProgress.fromJson(Map<String, dynamic> json) {
    final uuid = json['packageUuid'];
    return LessonProgress(
      packageUuid: uuid is String ? uuid : '',
      position: Duration(
        milliseconds: json['positionMs'] is num
            ? (json['positionMs'] as num).round().clamp(0, 1 << 31)
            : 0,
      ),
      lastOpenedAt: json['lastOpenedAt'] is String
          ? DateTime.tryParse(json['lastOpenedAt'] as String)
          : null,
      revealedCloze: (json['revealedCloze'] as List? ?? const [])
          .whereType<String>()
          .toSet(),
    );
  }
}

class LessonProgressStore {
  const LessonProgressStore();

  Future<Map<String, LessonProgress>> load() async {
    final file = await _file();
    if (!await file.exists()) return {};
    try {
      final decoded = jsonDecode(await file.readAsString());
      if (decoded is! Map<String, dynamic> || decoded['version'] != 1) {
        return {};
      }
      final records = decoded['records'];
      if (records is! List) return {};
      return {
        for (final record in records.whereType<Map<String, dynamic>>())
          if (LessonProgress.fromJson(record).packageUuid.isNotEmpty)
            LessonProgress.fromJson(record)
                .packageUuid: LessonProgress.fromJson(
              record,
            ),
      };
    } catch (_) {
      return {};
    }
  }

  Future<void> save(Map<String, LessonProgress> progress) async {
    final file = await _file();
    await file.parent.create(recursive: true);
    await file.writeAsString(
      const JsonEncoder.withIndent('  ').convert({
        'version': 1,
        'records': [for (final value in progress.values) value.toJson()],
      }),
      flush: true,
    );
  }

  Future<File> _file() async {
    final override = debugLessonProgressFile;
    if (override != null) return override;
    final directory = await intensiveListeningDataDirectory();
    return File(p.join(directory.path, 'lesson_progress.json'));
  }
}
