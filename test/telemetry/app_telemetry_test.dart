import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/telemetry/app_telemetry.dart';
import 'package:intensive_listening/transcription/transcription_queue.dart';

class _FakeTransport implements TelemetryTransport {
  int starts = 0;
  int stops = 0;
  final events = <(String, Map<String, String>)>[];
  final logs = <String>[];
  String cycle = 'installation-1';
  bool reachable = true;

  @override
  Future<bool> canReachCollector() async => reachable;

  @override
  Future<bool> start({
    required String version,
    required String cachePath,
  }) async {
    starts++;
    return true;
  }

  @override
  Future<void> stop({required String cachePath}) async {
    stops++;
    final cache = Directory(cachePath);
    if (await cache.exists()) await cache.delete(recursive: true);
  }

  @override
  Future<bool> event(String name, Map<String, String> fields) async {
    events.add((name, fields));
    return true;
  }

  @override
  Future<bool> errorLog(String text) async {
    logs.add(text);
    return true;
  }

  @override
  Future<String?> installCycle() async => cycle;

  @override
  Future<String?> installUuid() async => 'anonymous-installation';

  @override
  Future<Map<String, String>> systemProfile() async => {
    'windows_version': '10.0.26100',
    'cpu_arch': 'x64',
  };
}

void main() {
  late Directory root;
  late _FakeTransport transport;
  late AppTelemetry telemetry;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('ilp-telemetry-');
    transport = _FakeTransport();
    telemetry = AppTelemetry(
      transport: transport,
      dataDirectory: () async => root,
      supportedPlatform: true,
    );
  });

  tearDown(() async {
    if (await root.exists()) await root.delete(recursive: true);
  });

  test('consent gates startup, events, errors, and cache cleanup', () async {
    await telemetry.applyConsent(false, mainInstance: true);
    await telemetry.errorNotice('错误', '服务响应');
    expect(transport.starts, 0);
    expect(transport.events, isEmpty);
    expect(transport.logs, isEmpty);

    await telemetry.applyConsent(true, mainInstance: true);
    expect(transport.starts, 1);
    expect(transport.events.map((entry) => entry.$1), [
      'app_active',
      'system_profile',
    ]);

    final now = DateTime(2026, 9, 28);
    final completed = TranscriptionJob(
      id: 'one',
      title: 'audio',
      audioPath: r'C:\private\audio.mp3',
      status: TranscriptionJobStatus.completed,
      stage: TranscriptionStage.formatting,
      message: '',
      enqueuedAt: now,
      startedAt: now,
      finishedAt: now.add(const Duration(milliseconds: 275)),
      cacheProfile: jsonEncode({
        'provider': 'local',
        'localModel': r'C:\private\model.bin',
      }),
    );
    await telemetry.asrCompleted(completed, cacheHit: false);
    await telemetry.asrCompleted(completed, cacheHit: true);
    final asr = transport.events
        .where((entry) => entry.$1 == 'asr_completed')
        .toList();
    expect(asr.map((entry) => entry.$2['cache_hit']), ['false', 'true']);
    expect(asr.map((entry) => entry.$2['duration_ms']), ['275', '275']);
    expect(asr.map((entry) => entry.$2['model']), ['model.bin', 'model.bin']);
    expect(asr.map((entry) => entry.$2['api_host']), ['local', 'local']);
    await telemetry.errorNotice('错误 1', '响应 1');
    await telemetry.errorNotice('错误 2', '响应 2');
    expect(transport.logs, ['错误 1: 响应 1', '错误 2: 响应 2']);

    await telemetry.applyConsent(false, mainInstance: true);
    await telemetry.errorNotice('错误 3', '响应 3');
    expect(transport.stops, 2);
    expect(transport.logs.length, 2);
    expect(await Directory('${root.path}/cache/arms-rum').exists(), false);
  });

  test('system profile is once per installation cycle', () async {
    await telemetry.applyConsent(true, mainInstance: true);
    await telemetry.applyConsent(false, mainInstance: true);
    await telemetry.applyConsent(true, mainInstance: true);
    expect(
      transport.events.where((entry) => entry.$1 == 'system_profile').length,
      1,
    );
    transport.cycle = 'installation-2';
    await telemetry.applyConsent(false, mainInstance: true);
    await telemetry.applyConsent(true, mainInstance: true);
    expect(
      transport.events.where((entry) => entry.$1 == 'system_profile').length,
      2,
    );
  });

  test(
    'keeps a daily startup event pending while the collector is offline',
    () async {
      transport.reachable = false;
      await telemetry.applyConsent(true, mainInstance: true);
      expect(transport.events, isEmpty);
      final queue = File('${root.path}/telemetry/collect.json');
      expect(await queue.exists(), isTrue);

      transport.reachable = true;
      final nextLaunch = AppTelemetry(
        transport: transport,
        dataDirectory: () async => root,
        supportedPlatform: true,
      );
      await nextLaunch.applyConsent(true, mainInstance: true);
      expect(
        transport.events.where((event) => event.$1 == 'app_active').length,
        1,
      );
    },
  );
}
