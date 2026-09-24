import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/asr/asr_client.dart';
import 'package:intensive_listening/cancellation.dart';
import 'package:intensive_listening/transcription/duplicate_match.dart';
import 'package:intensive_listening/transcription/queue_store.dart';
import 'package:intensive_listening/transcription/srt_recognition_cache.dart';
import 'package:intensive_listening/transcription/transcription_queue.dart';

class FakeAsrRunner {
  final audioFiles = <File>[];
  final progressCallbacks = <AsrProgressCallback?>[];
  final _pending = <Completer<String>>[];

  Future<String> call({
    required File audioFile,
    AsrProgressCallback? onProgress,
    Future<void>? abortTrigger,
    Duration? estimatedProcessingTime,
  }) {
    audioFiles.add(audioFile);
    progressCallbacks.add(onProgress);
    final completer = Completer<String>();
    _pending.add(completer);
    if (abortTrigger != null) {
      unawaited(
        abortTrigger.then((_) {
          if (!completer.isCompleted) {
            completer.completeError(const OperationCanceled());
          }
        }),
      );
    }
    return completer.future;
  }

  int get callCount => _pending.length;

  void completeNext(String srt) {
    _pending.firstWhere((entry) => !entry.isCompleted).complete(srt);
  }

  void failNext(Object error) {
    _pending.firstWhere((entry) => !entry.isCompleted).completeError(error);
  }

  void emitProgress(AsrProgress progress) {
    progressCallbacks.lastWhere((callback) => callback != null)?.call(progress);
  }
}

void main() {
  late Directory temporaryDirectory;
  late QueueStore store;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('queue-test-');
    store = QueueStore(
      resolveFile: () async => File('${temporaryDirectory.path}/queue.json'),
    );
  });

  tearDown(() async {
    for (var attempt = 0; attempt < 20; attempt++) {
      if (!await temporaryDirectory.exists()) return;
      try {
        await temporaryDirectory.delete(recursive: true);
        return;
      } on PathAccessException {
        if (attempt == 19) rethrow;
        await Future<void>.delayed(const Duration(milliseconds: 25));
      }
    }
  });

  Future<void> settle() async {
    // Hashing and flushed file writes are noticeably slower on Windows CI.
    // Keep this helper deterministic without relying on a desktop event loop.
    await Future<void>.delayed(const Duration(milliseconds: 500));
  }

  Future<void> waitUntil(
    bool Function() condition, {
    Duration timeout = const Duration(seconds: 3),
  }) async {
    final deadline = DateTime.now().add(timeout);
    while (!condition()) {
      if (DateTime.now().isAfter(deadline)) {
        fail('Timed out waiting for asynchronous queue state.');
      }
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
  }

  Future<File> audioFile(String name) async {
    final file = File('${temporaryDirectory.path}/$name');
    await file.writeAsBytes(utf8.encode('audio-content-$name'), flush: true);
    return file;
  }

  TranscriptionQueue buildQueue({
    required FakeAsrRunner runner,
    DuplicateResolver? resolveDuplicate,
    String cacheProfile = 'profile-a',
  }) {
    return TranscriptionQueue(
      runner: runner.call,
      resolveDuplicate: resolveDuplicate ?? (_) async => null,
      store: store,
      cache: SrtRecognitionCache(
        resolveDirectory: () async =>
            Directory('${temporaryDirectory.path}/cache'),
      ),
      resolveCacheProfile: () => cacheProfile,
    );
  }

  test('runs a job to completion and exposes the transcript', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    expect(queue.jobById(id)?.status, TranscriptionJobStatus.running);

    await settle();
    expect(runner.audioFiles, hasLength(1));
    expect(queue.jobById(id)?.status, TranscriptionJobStatus.running);

    runner.completeNext('SRT-A');
    await waitUntil(
      () => queue.jobById(id)?.status == TranscriptionJobStatus.completed,
    );

    final job = queue.jobById(id);
    expect(job?.status, TranscriptionJobStatus.completed);
    expect(job?.srt, 'SRT-A');
    expect(queue.latestUnconsumedSrt?.id, id);
    expect(queue.isBusy, isFalse);
  });

  test(
    'empty recognition completes normally and is restored from cache',
    () async {
      final runner = FakeAsrRunner();
      final queue = buildQueue(runner: runner);
      addTearDown(queue.disposeQueue);
      final audio = await audioFile('silence.mp3');

      final first = queue.enqueue(title: 'Silent', audio: audio);
      await settle();
      runner.completeNext('');
      await settle();

      expect(queue.jobById(first)?.status, TranscriptionJobStatus.completed);
      expect(queue.jobById(first)?.message, contains('没有可用语音'));
      expect(queue.latestUnconsumedSrt, isNull);

      final second = queue.enqueue(title: 'Retry', audio: audio);
      await settle();
      expect(queue.jobById(second)?.status, TranscriptionJobStatus.completed);
      expect(queue.jobById(second)?.message, contains('缓存'));
      expect(runner.callCount, 1);
    },
  );

  test('reports monotonic progress across the transcription stages', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(
      title: 'Progress',
      audio: await audioFile('progress.mp3'),
      projectId: 'project-1',
    );
    await settle();

    final fractions = <double>[];
    void emit(AsrProgress progress) {
      runner.emitProgress(progress);
      fractions.add(queue.jobById(id)!.fraction!);
    }

    emit(const AsrProgress(stage: AsrStage.decoding));
    emit(const AsrProgress(stage: AsrStage.slicing));
    emit(const AsrProgress(stage: AsrStage.uploading, stageFraction: 0.5));
    emit(const AsrProgress(stage: AsrStage.recognizing, stageFraction: 0.5));
    emit(const AsrProgress(stage: AsrStage.formatting));
    emit(const AsrProgress(stage: AsrStage.merging));

    expect(fractions, orderedEquals([...fractions]..sort()));
    expect(fractions.last, 0.99);
    expect(queue.jobById(id)?.projectId, 'project-1');
  });

  test('runs queued jobs strictly one at a time', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    queue.enqueue(title: 'B', audio: await audioFile('b.mp3'));

    await settle();
    expect(runner.callCount, 1);

    runner.completeNext('SRT-A');
    await settle();
    expect(runner.callCount, 2);

    runner.completeNext('SRT-B');
    await settle();
    expect(queue.jobs.every((job) => job.isTerminal), isTrue);
  });

  test('records a failure with the runner message', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();
    runner.failNext(const AsrException('服务端拒绝'));
    await settle();

    final job = queue.jobById(id);
    expect(job?.status, TranscriptionJobStatus.failed);
    expect(job?.message, '服务端拒绝');
  });

  test('treats cancellation as canceled rather than failed', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();
    queue.cancel(id);
    await settle();

    expect(queue.jobById(id)?.status, TranscriptionJobStatus.canceled);
  });

  test('holds a job for a decision and continues when allowed', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(
      runner: runner,
      resolveDuplicate: (sha256) async => const DuplicateMatch(
        duplicateCase: DuplicateCase.sharedAudioLesson,
        existingTitle: '已有课程',
      ),
    );
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();

    expect(queue.jobById(id)?.status, TranscriptionJobStatus.awaitingDecision);
    expect(runner.callCount, 0);
    expect(queue.pendingDecisions, hasLength(1));

    queue.resolve(id, proceed: true);
    await settle();

    expect(runner.callCount, 1);
    runner.completeNext('SRT-A');
    await settle();
    expect(queue.jobById(id)?.status, TranscriptionJobStatus.completed);
  });

  test('cancels a job when the duplicate decision is refused', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(
      runner: runner,
      resolveDuplicate: (sha256) async => const DuplicateMatch(
        duplicateCase: DuplicateCase.sharedAudioLesson,
        existingTitle: '已有课程',
      ),
    );
    addTearDown(queue.disposeQueue);

    final id = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();
    queue.resolve(id, proceed: false);
    await settle();

    expect(queue.jobById(id)?.status, TranscriptionJobStatus.canceled);
    expect(runner.callCount, 0);
  });

  test('does not let a pending decision block the next job', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(
      runner: runner,
      resolveDuplicate: (sha256) async => const DuplicateMatch(
        duplicateCase: DuplicateCase.sharedAudioLesson,
        existingTitle: '已有课程',
      ),
    );
    addTearDown(queue.disposeQueue);

    queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    queue.enqueue(title: 'B', audio: await audioFile('b.mp3'));
    await settle();

    expect(queue.pendingDecisions, hasLength(2));
    expect(runner.callCount, 0);
  });

  test(
    'keeps only the newest finished jobs past the retention limit',
    () async {
      final runner = FakeAsrRunner();
      final queue = TranscriptionQueue(
        runner: runner.call,
        resolveDuplicate: (_) async => null,
        store: store,
        cache: SrtRecognitionCache(
          resolveDirectory: () async =>
              Directory('${temporaryDirectory.path}/cache'),
        ),
        resolveCacheProfile: () => 'profile-a',
        maxRetained: 1,
      );
      addTearDown(queue.disposeQueue);

      final first = queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
      await settle();
      runner.completeNext('SRT-A');
      await settle();

      final second = queue.enqueue(title: 'B', audio: await audioFile('b.mp3'));
      await settle();
      runner.completeNext('SRT-B');
      await settle();

      expect(queue.jobById(first), isNull);
      expect(queue.jobById(second)?.status, TranscriptionJobStatus.completed);
    },
  );

  test('restores unfinished jobs as interrupted and resumes them', () async {
    final runner = FakeAsrRunner();
    await store.save([
      StoredJob(
        id: 'restored-1',
        title: '中断的课程',
        audioPath: (await audioFile('c.mp3')).path,
        status: 'running',
        stage: 'recognizing',
        message: '服务器正在识别。',
        enqueuedAt: DateTime.now(),
      ),
    ]);

    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    await queue.restore();
    expect(
      queue.jobById('restored-1')?.status,
      TranscriptionJobStatus.interrupted,
    );
    expect(queue.interruptedCount, 1);
    expect(runner.callCount, 0);

    queue.resumeInterrupted();
    await settle();
    expect(queue.jobById('restored-1')?.status, TranscriptionJobStatus.running);
    expect(runner.callCount, 1);
  });

  test('stops notifying after disposal', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);

    var notifications = 0;
    queue.addListener(() => notifications++);

    queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();
    await queue.disposeQueue();
    final before = notifications;

    await settle();

    expect(notifications, before);
  });

  test(
    'restores matching audio from cache before duplicate detection',
    () async {
      final runner = FakeAsrRunner();
      final queue = buildQueue(runner: runner);
      addTearDown(queue.disposeQueue);

      final shared = await audioFile('shared.mp3');
      queue.enqueue(title: 'A', audio: shared);
      await settle();
      runner.completeNext('SRT-A');
      await settle();

      queue.enqueue(title: 'B', audio: shared);
      await settle();

      final second = queue.jobs.firstWhere((job) => job.title == 'B');
      expect(second.status, TranscriptionJobStatus.completed);
      expect(second.srt, 'SRT-A');
      expect(second.message, contains('缓存'));
      expect(runner.callCount, 1);
    },
  );

  test('does not reuse a cached transcript across ASR profiles', () async {
    final audio = await audioFile('profile.mp3');
    final firstRunner = FakeAsrRunner();
    final firstQueue = buildQueue(
      runner: firstRunner,
      cacheProfile: 'model-a/en',
    );
    addTearDown(firstQueue.disposeQueue);

    firstQueue.enqueue(title: 'A', audio: audio);
    await settle();
    firstRunner.completeNext('SRT-A');
    await settle();

    final secondRunner = FakeAsrRunner();
    final secondQueue = buildQueue(
      runner: secondRunner,
      cacheProfile: 'model-b/en',
    );
    addTearDown(secondQueue.disposeQueue);
    secondQueue.enqueue(title: 'B', audio: audio);
    await settle();

    expect(secondRunner.callCount, 1);
    expect(secondQueue.jobs.first.status, TranscriptionJobStatus.running);
  });

  test('writes the queue to disk without an API key', () async {
    final runner = FakeAsrRunner();
    final queue = buildQueue(runner: runner);
    addTearDown(queue.disposeQueue);

    queue.enqueue(title: 'A', audio: await audioFile('a.mp3'));
    await settle();

    final file = File('${temporaryDirectory.path}/queue.json');
    final contents = await file.readAsString();

    expect(contents, contains('a.mp3'));
    expect(contents, isNot(contains('apiKey')));
    expect(contents, isNot(contains('Authorization')));
    expect(contents, isNot(contains('Bearer')));
  });
}
