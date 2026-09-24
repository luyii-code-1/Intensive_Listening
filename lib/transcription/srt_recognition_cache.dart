import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';

const _cacheFormatVersion = 1;

class SrtRecognitionCache {
  SrtRecognitionCache({Future<Directory> Function()? resolveDirectory})
    : _resolveDirectory = resolveDirectory ?? _defaultDirectory;

  final Future<Directory> Function() _resolveDirectory;

  static Future<Directory> _defaultDirectory() async {
    final data = await intensiveListeningDataDirectory();
    return Directory(p.join(data.path, 'cache', 'asr-srt'));
  }

  Future<String?> read({
    required String audioMd5,
    required String profile,
  }) async {
    try {
      final files = await _files(audioMd5, profile);
      if (!await files.srt.exists() || !await files.metadata.exists()) {
        return null;
      }
      final decoded = jsonDecode(await files.metadata.readAsString());
      if (decoded is! Map<String, dynamic> ||
          decoded['version'] != _cacheFormatVersion ||
          decoded['audioMd5'] != audioMd5.toLowerCase() ||
          decoded['profileMd5'] != files.profileMd5) {
        return null;
      }
      return await files.srt.readAsString();
    } catch (_) {
      return null;
    }
  }

  Future<void> write({
    required String audioMd5,
    required String profile,
    required String srt,
  }) async {
    try {
      final files = await _files(audioMd5, profile);
      await files.srt.parent.create(recursive: true);
      await files.srt.writeAsString(srt, flush: true);
      await files.metadata.writeAsString(
        jsonEncode({
          'version': _cacheFormatVersion,
          'audioMd5': audioMd5.toLowerCase(),
          'profileMd5': files.profileMd5,
          'createdAt': DateTime.now().toUtc().toIso8601String(),
        }),
        flush: true,
      );
    } catch (_) {
      // A cache write must never turn a successful transcription into a failure.
    }
  }

  Future<_CacheFiles> _files(String audioMd5, String profile) async {
    final normalizedMd5 = audioMd5.toLowerCase();
    if (!RegExp(r'^[a-f0-9]{32}$').hasMatch(normalizedMd5)) {
      throw const FormatException('Invalid audio MD5');
    }
    final root = await _resolveDirectory();
    final profileMd5 = md5.convert(utf8.encode(profile)).toString();
    final directory = Directory(p.join(root.path, normalizedMd5));
    return _CacheFiles(
      srt: File(p.join(directory.path, '$profileMd5.srt')),
      metadata: File(p.join(directory.path, '$profileMd5.json')),
      profileMd5: profileMd5,
    );
  }
}

class _CacheFiles {
  const _CacheFiles({
    required this.srt,
    required this.metadata,
    required this.profileMd5,
  });

  final File srt;
  final File metadata;
  final String profileMd5;
}
