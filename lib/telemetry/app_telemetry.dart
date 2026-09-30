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
  Future<String?> installUuid();
  Future<Map<String, String>> systemProfile();
  Future<bool> canReachCollector();
}

class WindowsTelemetryTransport implements TelemetryTransport {
  const WindowsTelemetryTransport();

  static const _channel = MethodChannel('intensive_listening/telemetry');
  static const _collectorHost = 'hm3xyft6jd-default-cn.rum.aliyuncs.com';

  @override
  Future<bool> canReachCollector() async {
    try {
      final socket = await Socket.connect(
        _collectorHost,
        443,
        timeout: const Duration(seconds: 3),
      );
      socket.destroy();
      return true;
    } catch (_) {
      return false;
    }
  }

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
  Future<String?> installUuid() => _channel.invokeMethod<String>('installUuid');

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
  bool _launchReported = false;

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
            final pending = File(
              p.join(root.path, 'telemetry', 'collect.json'),
            );
            if (await pending.exists()) await pending.delete();
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
          await _reportAppLaunch();
          final reachable = await _transport.canReachCollector();
          await _reportAppActive(root, reachable: reachable);
          if (reachable) await _reportSystemProfile(root);
        })
        .catchError((Object error, StackTrace stack) {
          AppLog.warning('遥测状态切换失败: $error', stack);
        });
    return _transition;
  }

  Future<void> _reportAppLaunch() async {
    if (_launchReported || !_enabled || !_consent) return;
    final installUuid = await _transport.installUuid();
    if (!_enabled || !_consent) return;
    _launchReported = await _transport.event('app_launch', {
      'app_version': appVersion,
      if (installUuid != null && installUuid.isNotEmpty)
        'install_uuid': installUuid,
    });
  }

  Future<void> _reportAppActive(
    Directory root, {
    required bool reachable,
  }) async {
    if (_activeReported) return;
    final file = File(p.join(root.path, 'telemetry', 'collect.json'));
    final today = DateTime.now().toIso8601String().substring(0, 10);
    final key = '$today|$appVersion';
    final pending = <String>[];
    final submitted = <String>{};
    if (await file.exists()) {
      try {
        final saved = jsonDecode(await file.readAsString());
        if (saved is Map<String, dynamic>) {
          pending.addAll(
            (saved['pending'] as List?)?.whereType<String>() ?? const [],
          );
          submitted.addAll(
            (saved['submitted'] as List?)?.whereType<String>() ?? const [],
          );
        }
      } catch (_) {
        AppLog.warning('启动遥测队列读取失败，将重新建立');
      }
    }
    if (!submitted.contains(key) && !pending.contains(key)) pending.add(key);
    await file.parent.create(recursive: true);
    Future<void> save() => file.writeAsString(
      jsonEncode({'pending': pending, 'submitted': submitted.toList()}),
      flush: true,
    );
    await save();
    if (!reachable || !_enabled || !_consent) return;
    final installUuid = await _transport.installUuid();
    for (final entry in pending.toList()) {
      final parts = entry.split('|');
      if (parts.length != 2) continue;
      final accepted = await _transport.event('app_active', {
        'active_date': parts[0],
        'app_version': parts[1],
        if (installUuid != null && installUuid.isNotEmpty)
          'install_uuid': installUuid,
      });
      if (!accepted) break;
      pending.remove(entry);
      submitted.add(entry);
      if (entry == key) _activeReported = true;
      await save();
    }
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
    String apiHost = 'unknown';
    try {
      final profile =
          jsonDecode(job.cacheProfile ?? '') as Map<String, dynamic>;
      final local = profile['provider'] == 'local';
      model = local
          ? p.basename('${profile['localModel'] ?? ''}'.replaceAll('\\', '/'))
          : '${profile['model'] ?? ''}';
      if (local) {
        apiHost = 'local';
      } else {
        final base = '${profile['baseUrl'] ?? ''}'.trim();
        final endpoint = '${profile['endpoint'] ?? ''}'.trim();
        final host =
            (Uri.tryParse(base)?.host.isNotEmpty == true
                ? Uri.tryParse(base)?.host
                : Uri.tryParse(endpoint)?.host) ??
            '';
        if (RegExp(r'^[A-Za-z0-9.-]{1,253}$').hasMatch(host)) {
          apiHost = host.toLowerCase();
        }
      }
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
      'api_host': apiHost,
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
