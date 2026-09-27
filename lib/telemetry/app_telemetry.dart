import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';
import '../app_log.dart';
import '../transcription/transcription_queue.dart';

abstract class TelemetryTransport {
  Future<bool> start({required String version, required String cachePath});
  Future<void> stop({required String cachePath});
  Future<bool> event(String name, Map<String, String> fields);
  Future<bool> errorLog(String text);
  Future<String?> installCycle();
  Future<Map<String, String>> systemProfile();
}

class WindowsTelemetryTransport implements TelemetryTransport {
  const WindowsTelemetryTransport();

  static const _channel = MethodChannel('intensive_listening/telemetry');

  @override
  Future<bool> start({
    required String version,
    required String cachePath,
  }) async =>
      await _channel.invokeMethod<bool>('start', {
        'version': version,
        'cachePath': cachePath,
      }) ??
      false;

  @override
  Future<void> stop({required String cachePath}) =>
      _channel.invokeMethod<void>('stop', {'cachePath': cachePath});

  @override
  Future<bool> event(String name, Map<String, String> fields) async =>
      await _channel.invokeMethod<bool>('event', {
        'name': name,
        'fields': fields,
      }) ??
      false;

  @override
  Future<bool> errorLog(String text) async =>
      await _channel.invokeMethod<bool>('errorLog', {'text': text}) ?? false;

  @override
  Future<String?> installCycle() =>
      _channel.invokeMethod<String>('installCycle');

  @override
  Future<Map<String, String>> systemProfile() async {
    final data = await _channel.invokeMapMethod<String, String>(
      'systemProfile',
    );
    return data ?? const {};
  }
}

class AppTelemetry {
  AppTelemetry({
    TelemetryTransport? transport,
    Future<Directory> Function()? dataDirectory,
    bool? supportedPlatform,
  }) : _transport = transport ?? const WindowsTelemetryTransport(),
       _dataDirectory = dataDirectory ?? intensiveListeningDataDirectory,
       _supportedPlatform =
           supportedPlatform ?? (!kIsWeb && Platform.isWindows);

  static final instance = AppTelemetry();

  final TelemetryTransport _transport;
  final Future<Directory> Function() _dataDirectory;
  final bool _supportedPlatform;
  Future<void> _transition = Future<void>.value();
  bool _enabled = false;
  bool _consent = false;
  bool _activeReported = false;

  bool get enabled => _enabled;

  Future<void> applyConsent(bool consent, {required bool mainInstance}) {
    _consent = consent && mainInstance && _supportedPlatform;
    if (!_consent) _enabled = false;
    _transition = _transition
        .then((_) async {
          if (!mainInstance || !_supportedPlatform) return;
          final root = await _dataDirectory();
          final cache = Directory(p.join(root.path, 'cache', 'arms-rum'));
          if (!_consent) {
            await _transport.stop(cachePath: cache.path);
            return;
          }
          if (_enabled) return;
          await cache.create(recursive: true);
          if (!_consent) return;
          _enabled = await _transport.start(
            version: appVersion,
            cachePath: cache.path,
          );
          if (!_enabled) {
            AppLog.warning('遥测组件未就绪，数据未上报');
            return;
          }
          if (!_consent) {
            _enabled = false;
            await _transport.stop(cachePath: cache.path);
            return;
          }
          if (!_activeReported) {
            _activeReported = await _transport.event('app_active', const {});
          }
          await _reportSystemProfile(root);
        })
        .catchError((Object error, StackTrace stack) {
          AppLog.warning('遥测状态切换失败: $error', stack);
        });
    return _transition;
  }

  Future<void> _reportSystemProfile(Directory root) async {
    final cycle = await _transport.installCycle();
    if (cycle == null || cycle.isEmpty) return;
    final marker = File(
      p.join(root.path, 'telemetry', 'system-profile-cycle.txt'),
    );
    if (await marker.exists() &&
        (await marker.readAsString()).trim() == cycle) {
      return;
    }
    final fields = {
      ...await _transport.systemProfile(),
      'app_version': appVersion,
    };
    if (fields.isEmpty || !_enabled || !_consent) return;
    if (!await _transport.event('system_profile', fields)) return;
    await marker.parent.create(recursive: true);
    await marker.writeAsString(cycle, flush: true);
  }

  Future<void> asrCompleted(TranscriptionJob job, {required bool cacheHit}) {
    if (!_enabled || job.status != TranscriptionJobStatus.completed) {
      return Future<void>.value();
    }
    final started = job.startedAt;
    final finished = job.finishedAt;
    if (started == null || finished == null) return Future<void>.value();
    String model = '';
    try {
      final profile =
          jsonDecode(job.cacheProfile ?? '') as Map<String, dynamic>;
      model = profile['provider'] == 'local'
          ? p.basename('${profile['localModel'] ?? ''}'.replaceAll('\\', '/'))
          : '${profile['model'] ?? ''}';
    } catch (_) {
      model = 'unknown';
    }
    if (model.contains('/') || model.contains('\\') || model.contains(':')) {
      model = 'unknown';
    }
    final duration = finished.difference(started).inMilliseconds;
    return _sendEvent('asr_completed', {
      'cache_hit': '$cacheHit',
      'model': model,
      'duration_ms': '${duration < 0 ? 0 : duration}',
    });
  }

  Future<void> errorNotice(String title, String message) {
    if (!_enabled) return Future<void>.value();
    return _sendError('$title: $message');
  }

  Future<void> _sendEvent(String name, Map<String, String> fields) async {
    try {
      if (_enabled) await _transport.event(name, fields);
    } catch (error, stack) {
      AppLog.warning('遥测事件提交失败: $error', stack);
    }
  }

  Future<void> _sendError(String text) async {
    try {
      if (_enabled) await _transport.errorLog(text);
    } catch (error, stack) {
      AppLog.warning('错误提示遥测提交失败: $error', stack);
    }
  }
}
