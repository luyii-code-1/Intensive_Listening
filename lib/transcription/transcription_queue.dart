import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';

import '../asr/asr_client.dart';
import '../cancellation.dart';
import '../ilp/file_hashing.dart';
import 'duplicate_match.dart';
import 'queue_store.dart';
import 'srt_recognition_cache.dart';

enum TranscriptionJobStatus {
  queued,
  running,
  awaitingDecision,
  completed,
  failed,
  canceled,
  interrupted,
}

enum TranscriptionStage {
  queued,
  fingerprinting,
  decoding,
  slicing,
  uploading,
  recognizing,
  formatting,
  merging,
}

typedef AsrRunner = Future<String> Function({
  required File audioFile,
  AsrProgressCallback? onProgress,
  Future<void>? abortTrigger,
  Duration? estimatedProcessingTime,
});

typedef DuplicateResolver = Future<DuplicateMatch?> Function(String sha256);
typedef CacheProfileResolver = String Function();

const _unset = Object();

const _processingTimeFactor = 0.35;
const _minProcessingEstimate = Duration(seconds: 20);
const _maxProcessingEstimate = Duration(hours: 1);

class TranscriptionJob {
  const TranscriptionJob({
    required this.id,
    required this.title,
    required this.audioPath,
    required this.status,
    required this.stage,
    required this.message,
    required this.enqueuedAt,
    this.projectId,
    this.fraction,
    this.fractionIsEstimated = false,
    this.bytesDone = 0,
    this.bytesTotal = 0,
    this.segmentIndex,
    this.segmentTotal,
    this.sha256,
    this.audioMd5,
    this.cacheProfile,
    this.duplicate,
    this.duplicateApproved = false,
    this.audioDuration,
    this.startedAt,
    this.finishedAt,
    this.srt,
    this.srtConsumed = false,
  });

  final String id;
  final String title;
  final String audioPath;
  final TranscriptionJobStatus status;
  final TranscriptionStage stage;
  final String message;
  final DateTime enqueuedAt;
  final String? projectId;
  final double? fraction;
  final bool fractionIsEstimated;
  final int bytesDone;
  final int bytesTotal;
  final int? segmentIndex;
  final int? segmentTotal;
  final String? sha256;
  final String? audioMd5;
  final String? cacheProfile;
  final DuplicateMatch? duplicate;
  final bool duplicateApproved;
  final Duration? audioDuration;
  final DateTime? startedAt;
  final DateTime? finishedAt;
  final String? srt;
  final bool srtConsumed;

  bool get isTerminal =>
      status == TranscriptionJobStatus.completed ||
      status == TranscriptionJobStatus.failed ||
      status == TranscriptionJobStatus.canceled;

  bool get isActive =>
      status == TranscriptionJobStatus.queued ||
      status == TranscriptionJobStatus.running ||
      status == TranscriptionJobStatus.awaitingDecision;

  TranscriptionJob copyWith({
    String? title,
    TranscriptionJobStatus? status,
    TranscriptionStage? stage,
    String? message,
    Object? fraction = _unset,
    bool? fractionIsEstimated,
    int? bytesDone,
    int? bytesTotal,
    Object? segmentIndex = _unset,
    Object? segmentTotal = _unset,
    Object? sha256 = _unset,
    Object? audioMd5 = _unset,
    Object? cacheProfile = _unset,
    Object? duplicate = _unset,
    bool? duplicateApproved,
    Object? audioDuration = _unset,
    Object? startedAt = _unset,
    Object? finishedAt = _unset,
    Object? srt = _unset,
    bool? srtConsumed,
  }) {
    return TranscriptionJob(
      id: id,
      title: title ?? this.title,
      audioPath: audioPath,
      status: status ?? this.status,
      stage: stage ?? this.stage,
      message: message ?? this.message,
      enqueuedAt: enqueuedAt,
      projectId: projectId,
      fraction: identical(fraction, _unset)
          ? this.fraction
          : fraction as double?,
      fractionIsEstimated: fractionIsEstimated ?? this.fractionIsEstimated,
      bytesDone: bytesDone ?? this.bytesDone,
      bytesTotal: bytesTotal ?? this.bytesTotal,
      segmentIndex: identical(segmentIndex, _unset)
          ? this.segmentIndex
          : segmentIndex as int?,
      segmentTotal: identical(segmentTotal, _unset)
          ? this.segmentTotal
          : segmentTotal as int?,
      sha256: identical(sha256, _unset) ? this.sha256 : sha256 as String?,
      audioMd5: identical(audioMd5, _unset)
          ? this.audioMd5
          : audioMd5 as String?,
      cacheProfile: identical(cacheProfile, _unset)
          ? this.cacheProfile
          : cacheProfile as String?,
      duplicate: identical(duplicate, _unset)
          ? this.duplicate
          : duplicate as DuplicateMatch?,
      duplicateApproved: duplicateApproved ?? this.duplicateApproved,
      audioDuration: identical(audioDuration, _unset)
          ? this.audioDuration
          : audioDuration as Duration?,
      startedAt: identical(startedAt, _unset)
          ? this.startedAt
          : startedAt as DateTime?,
      finishedAt: identical(finishedAt, _unset)
          ? this.finishedAt
          : finishedAt as DateTime?,
      srt: identical(srt, _unset) ? this.srt : srt as String?,
      srtConsumed: srtConsumed ?? this.srtConsumed,
    );
  }
}

class TranscriptionQueue extends ChangeNotifier {
  TranscriptionQueue({
    required this.runner,
    required this.resolveDuplicate,
    required this.store,
    required this.cache,
    required this.resolveCacheProfile,
    this.maxConcurrent = 1,
    this.maxRetained = 50,
  });

  final AsrRunner runner;
  final DuplicateResolver resolveDuplicate;
  final QueueStore store;
  final SrtRecognitionCache cache;
  final CacheProfileResolver resolveCacheProfile;
  final int maxConcurrent;
  final int maxRetained;

  final Map<String, Completer<void>> _abortTriggers = {};
  var _jobs = <TranscriptionJob>[];
  var _disposed = false;
  var _sequence = 0;

  List<TranscriptionJob> get jobs => List.unmodifiable(_jobs);

  bool get isBusy => _jobs.any((job) => job.isActive);

  int get interruptedCount => _jobs
      .where((job) => job.status == TranscriptionJobStatus.interrupted)
      .length;

  List<TranscriptionJob> get pendingDecisions => _jobs
      .where((job) => job.status == TranscriptionJobStatus.awaitingDecision)
      .toList(growable: false);

  TranscriptionJob? jobById(String jobId) {
    for (final job in _jobs) {
      if (job.id == jobId) return job;
    }
    return null;
  }

  TranscriptionJob? get latestUnconsumedSrt {
    for (final job in _jobs) {
      if (job.status == TranscriptionJobStatus.completed &&
          !job.srtConsumed &&
          (job.srt?.isNotEmpty ?? false)) {
        return job;
      }
    }
    return null;
  }

  String enqueue({
    required String title,
    required File audio,
    Duration? audioDuration,
    String? projectId,
  }) {
    _sequence += 1;
    final job = TranscriptionJob(
      id: '${DateTime.now().microsecondsSinceEpoch}-$_sequence',
      title: title,
      audioPath: audio.path,
      status: TranscriptionJobStatus.queued,
      stage: TranscriptionStage.queued,
      message: '等待前面的任务完成。',
      audioDuration: audioDuration,
      enqueuedAt: DateTime.now(),
      projectId: projectId,
      cacheProfile: resolveCacheProfile(),
    );
    _jobs = [job, ..._jobs];
    unawaited(_persist());
    if (!_disposed) notifyListeners();
    _pump();
    return job.id;
  }

  void resolve(String jobId, {required bool proceed}) {
    final job = jobById(jobId);
    if (job == null || job.status != TranscriptionJobStatus.awaitingDecision) {
      return;
    }
    if (!proceed) {
      _update(
        jobId,
        (current) => current.copyWith(
          status: TranscriptionJobStatus.canceled,
          finishedAt: DateTime.now(),
          fraction: null,
          fractionIsEstimated: false,
          duplicate: null,
          message: '任务已取消。',
        ),
        persist: true,
      );
      _trimRetained();
      return;
    }
    _update(
      jobId,
      (current) => current.copyWith(
        status: TranscriptionJobStatus.queued,
        stage: TranscriptionStage.queued,
        fraction: null,
        fractionIsEstimated: false,
        duplicate: null,
        duplicateApproved: true,
        message: '等待前面的任务完成。',
      ),
      persist: true,
    );
    _pump();
  }

  void cancel(String jobId) {
    final job = jobById(jobId);
    if (job == null) return;
    switch (job.status) {
      case TranscriptionJobStatus.queued:
      case TranscriptionJobStatus.awaitingDecision:
      case TranscriptionJobStatus.interrupted:
        _update(
          jobId,
          (current) => current.copyWith(
            status: TranscriptionJobStatus.canceled,
            fraction: null,
            fractionIsEstimated: false,
            duplicate: null,
            finishedAt: DateTime.now(),
            message: '任务已取消。',
          ),
          persist: true,
        );
        _trimRetained();
        _pump();
      case TranscriptionJobStatus.running:
        _update(jobId, (current) => current.copyWith(message: '正在取消。'));
        final trigger = _abortTriggers[jobId];
        if (trigger != null && !trigger.isCompleted) trigger.complete();
      case TranscriptionJobStatus.completed:
      case TranscriptionJobStatus.failed:
      case TranscriptionJobStatus.canceled:
        break;
    }
  }

  void markSrtConsumed(String jobId) {
    _update(jobId, (current) => current.copyWith(srtConsumed: true, srt: null));
    unawaited(_persist());
  }

  void remove(String jobId) {
    final job = jobById(jobId);
    if (job == null || !job.isTerminal) return;
    _jobs = _jobs.where((entry) => entry.id != jobId).toList();
    unawaited(_persist());
    if (!_disposed) notifyListeners();
  }

  void delete(String jobId) {
    final job = jobById(jobId);
    if (job == null) return;
    final trigger = _abortTriggers[jobId];
    if (trigger != null && !trigger.isCompleted) trigger.complete();
    _jobs = _jobs.where((entry) => entry.id != jobId).toList();
    unawaited(_persist());
    if (!_disposed) notifyListeners();
    _pump();
  }

  void clearFinished() {
    _jobs = _jobs.where((job) => !job.isTerminal).toList();
    unawaited(_persist());
    if (!_disposed) notifyListeners();
  }

  Future<void> restore() async {
    final stored = await store.load();
    if (stored.isEmpty || _disposed) return;
    final restored = <TranscriptionJob>[];
    for (final entry in stored) {
      restored.add(
        TranscriptionJob(
          id: entry.id,
          title: entry.title,
          audioPath: entry.audioPath,
          status: _restoredStatus(entry.status),
          stage: _restoredStage(entry.stage),
          message: entry.message,
          enqueuedAt: entry.enqueuedAt,
          projectId: entry.projectId,
          startedAt: entry.startedAt,
          finishedAt: entry.finishedAt,
          sha256: entry.sha256,
          audioMd5: entry.audioMd5,
          cacheProfile: entry.cacheProfile,
          audioDuration: entry.audioDurationMs == null
              ? null
              : Duration(milliseconds: entry.audioDurationMs!),
          srt: entry.srt,
          srtConsumed: entry.srtConsumed,
          duplicateApproved: entry.duplicateApproved,
        ),
      );
    }
    _jobs = restored;
    if (!_disposed) notifyListeners();
  }

  void resumeInterrupted() {
    _jobs = [
      for (final job in _jobs)
        if (job.status == TranscriptionJobStatus.interrupted)
          job.copyWith(
            status: TranscriptionJobStatus.queued,
            stage: TranscriptionStage.queued,
            fraction: null,
            fractionIsEstimated: false,
            message: '等待前面的任务完成。',
          )
        else
          job,
    ];
    unawaited(_persist());
    if (!_disposed) notifyListeners();
    _pump();
  }

  Future<void> disposeQueue() async {
    _disposed = true;
    for (final trigger in _abortTriggers.values) {
      if (!trigger.isCompleted) trigger.complete();
    }
    _abortTriggers.clear();
    super.dispose();
  }

  static TranscriptionJobStatus _restoredStatus(String value) {
    return switch (value) {
      'completed' => TranscriptionJobStatus.completed,
      'failed' => TranscriptionJobStatus.failed,
      'canceled' => TranscriptionJobStatus.canceled,
      _ => TranscriptionJobStatus.interrupted,
    };
  }

  static TranscriptionStage _restoredStage(String value) {
    for (final stage in TranscriptionStage.values) {
      if (stage.name == value) return stage;
    }
    return TranscriptionStage.queued;
  }

  void _pump() {
    if (_disposed) return;
    while (_runningCount() < maxConcurrent) {
      final next = _oldestQueued();
      if (next == null) return;
      _start(next);
    }
  }

  int _runningCount() =>
      _jobs.where((job) => job.status == TranscriptionJobStatus.running).length;

  TranscriptionJob? _oldestQueued() {
    for (final job in _jobs.reversed) {
      if (job.status == TranscriptionJobStatus.queued) return job;
    }
    return null;
  }

  void _start(TranscriptionJob job) {
    final trigger = Completer<void>();
    _abortTriggers[job.id] = trigger;
    _update(
      job.id,
      (current) => current.copyWith(
        status: TranscriptionJobStatus.running,
        stage: TranscriptionStage.fingerprinting,
        startedAt: DateTime.now(),
        fraction: null,
        fractionIsEstimated: false,
        bytesDone: 0,
        bytesTotal: 0,
        message: '正在计算音频指纹。',
      ),
      persist: true,
    );
    unawaited(_execute(job.id, trigger));
  }

  Future<void> _execute(String jobId, Completer<void> trigger) async {
    try {
      var job = jobById(jobId);
      if (job == null) return;
      final audio = File(job.audioPath);

      if (job.sha256 == null || job.audioMd5 == null) {
        final hashes = await hashFileMd5AndSha256(
          audio,
          isCanceled: () => trigger.isCompleted,
          onProgress: (done, total) => _update(
            jobId,
            (current) => current.copyWith(
              stage: TranscriptionStage.fingerprinting,
              fraction: total == 0 ? 0.05 : (done / total) * 0.05,
              bytesDone: done,
              bytesTotal: total,
              message: '正在计算音频指纹。',
            ),
          ),
        );
        _update(
          jobId,
          (current) => current.copyWith(
            sha256: hashes.sha256,
            audioMd5: hashes.md5,
            cacheProfile: current.cacheProfile ?? resolveCacheProfile(),
          ),
        );
        job = jobById(jobId);
        if (job == null) return;
      }

      final profile = job.cacheProfile ?? resolveCacheProfile();
      if (job.cacheProfile == null) {
        _update(jobId, (current) => current.copyWith(cacheProfile: profile));
        job = jobById(jobId);
        if (job == null) return;
      }
      final cachedSrt = await cache.read(
        audioMd5: job.audioMd5!,
        profile: profile,
      );
      if (cachedSrt != null) {
        _complete(jobId, cachedSrt, fromCache: true);
        return;
      }

      final duplicate = job.duplicateApproved
          ? null
          : await _findDuplicate(jobId, job.sha256!);
      if (duplicate != null) {
        _update(
          jobId,
          (current) => current.copyWith(
            status: TranscriptionJobStatus.awaitingDecision,
            stage: TranscriptionStage.queued,
            fraction: null,
            fractionIsEstimated: false,
            bytesDone: 0,
            bytesTotal: 0,
            duplicate: duplicate,
            message: '等待确认是否继续。',
          ),
          persist: true,
        );
        return;
      }

      await _transcribe(jobId, trigger);
    } on OperationCanceled {
      _update(
        jobId,
        (current) => current.copyWith(
          status: TranscriptionJobStatus.canceled,
          fraction: null,
          fractionIsEstimated: false,
          finishedAt: DateTime.now(),
          message: '任务已取消。',
        ),
        persist: true,
      );
    } catch (error) {
      _update(
        jobId,
        (current) => current.copyWith(
          status: TranscriptionJobStatus.failed,
          fraction: null,
          fractionIsEstimated: false,
          finishedAt: DateTime.now(),
          message: error is AsrException ? error.message : '转写失败：$error',
        ),
        persist: true,
      );
    } finally {
      _abortTriggers.remove(jobId);
      _trimRetained();
      unawaited(_persist());
      _pump();
    }
  }

  Future<void> _transcribe(String jobId, Completer<void> trigger) async {
    final job = jobById(jobId);
    if (job == null) return;

    _update(
      jobId,
      (current) => current.copyWith(
        stage: TranscriptionStage.uploading,
        fraction: 0.05,
        fractionIsEstimated: false,
        message: '正在发送音频。',
      ),
    );

    final srt = await runner(
      audioFile: File(job.audioPath),
      abortTrigger: trigger.future,
      estimatedProcessingTime: _estimateProcessingTime(job.audioDuration),
      onProgress: (progress) => _applyProgress(jobId, progress),
    );

    await cache.write(
      audioMd5: job.audioMd5!,
      profile: job.cacheProfile ?? resolveCacheProfile(),
      srt: srt,
    );

    _complete(jobId, srt);
  }

  void _complete(String jobId, String srt, {bool fromCache = false}) {
    _update(
      jobId,
      (current) => current.copyWith(
        status: TranscriptionJobStatus.completed,
        stage: TranscriptionStage.formatting,
        fraction: 1.0,
        fractionIsEstimated: false,
        srt: srt,
        srtConsumed: false,
        finishedAt: DateTime.now(),
        message: fromCache
            ? (srt.trim().isEmpty ? '已从缓存恢复，音频中没有可用语音。' : '已从缓存恢复 SRT。')
            : (srt.trim().isEmpty ? '识别完成，音频中没有可用语音。' : '字幕已写入 SRT。'),
      ),
      persist: true,
    );
  }

  void _applyProgress(String jobId, AsrProgress progress) {
    switch (progress.stage) {
      case AsrStage.decoding:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.decoding,
            fraction: 0.07,
            fractionIsEstimated: false,
            message: '正在分析音频。',
          ),
        );
      case AsrStage.slicing:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.slicing,
            fraction: 0.1,
            fractionIsEstimated: false,
            message: '正在按静音位置切分音频。',
          ),
        );
      case AsrStage.uploading:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.uploading,
            fraction: 0.1 + (progress.stageFraction ?? 0) * 0.05,
            fractionIsEstimated: false,
            bytesDone: progress.bytesDone,
            bytesTotal: progress.bytesTotal,
            message:
                '正在发送音频，${_megabytes(progress.bytesDone)} / '
                '${_megabytes(progress.bytesTotal)} MB。',
          ),
        );
      case AsrStage.recognizing:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.recognizing,
            fraction: 0.15 + (progress.stageFraction ?? 0) * 0.8,
            fractionIsEstimated: false,
            segmentIndex: progress.segmentIndex,
            segmentTotal: progress.segmentTotal,
            message: progress.segmentTotal == null ? '服务器正在识别。' : '正在并行识别音频分段。',
          ),
        );
      case AsrStage.formatting:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.formatting,
            fraction: 0.97,
            fractionIsEstimated: false,
            message: '正在写入 SRT。',
          ),
        );
      case AsrStage.merging:
        _update(
          jobId,
          (current) => current.copyWith(
            stage: TranscriptionStage.merging,
            fraction: 0.99,
            fractionIsEstimated: false,
            segmentIndex: progress.segmentIndex,
            segmentTotal: progress.segmentTotal,
            message: '正在合并字幕时间轴。',
          ),
        );
    }
  }

  Future<DuplicateMatch?> _findDuplicate(String jobId, String sha256) async {
    for (final other in _jobs) {
      if (other.id == jobId) continue;
      if (other.sha256 != sha256) continue;
      if (other.status == TranscriptionJobStatus.failed ||
          other.status == TranscriptionJobStatus.canceled ||
          other.status == TranscriptionJobStatus.interrupted ||
          (other.status == TranscriptionJobStatus.completed &&
              (other.srt?.trim().isEmpty ?? true))) {
        continue;
      }
      return DuplicateMatch(
        duplicateCase: DuplicateCase.sharedAudioJob,
        existingTitle: other.title.isEmpty ? '未命名任务' : other.title,
        jobId: other.id,
      );
    }
    return resolveDuplicate(sha256);
  }

  void _update(
    String jobId,
    TranscriptionJob Function(TranscriptionJob) transform, {
    bool persist = false,
  }) {
    final index = _jobs.indexWhere((job) => job.id == jobId);
    if (index < 0) return;
    final next = List<TranscriptionJob>.of(_jobs);
    next[index] = transform(next[index]);
    _jobs = next;
    if (persist) unawaited(_persist());
    if (!_disposed) notifyListeners();
  }

  void _trimRetained() {
    final terminal = _jobs.where((job) => job.isTerminal).toList();
    if (terminal.length <= maxRetained) return;
    final keep = terminal.take(maxRetained).map((job) => job.id).toSet();
    _jobs = _jobs
        .where((job) => !job.isTerminal || keep.contains(job.id))
        .toList();
  }

  Future<void> _persist() async {
    if (_disposed) return;
    await store.save([
      for (final job in _jobs)
        StoredJob(
          id: job.id,
          title: job.title,
          audioPath: job.audioPath,
          status: job.status.name,
          stage: job.stage.name,
          message: job.message,
          enqueuedAt: job.enqueuedAt,
          projectId: job.projectId,
          startedAt: job.startedAt,
          finishedAt: job.finishedAt,
          sha256: job.sha256,
          audioMd5: job.audioMd5,
          cacheProfile: job.cacheProfile,
          audioDurationMs: job.audioDuration?.inMilliseconds,
          srt: job.srt,
          srtConsumed: job.srtConsumed,
          duplicateApproved: job.duplicateApproved,
        ),
    ]);
  }

  static Duration? _estimateProcessingTime(Duration? audioDuration) {
    if (audioDuration == null || audioDuration <= Duration.zero) return null;
    final estimate = Duration(
      milliseconds: (audioDuration.inMilliseconds * _processingTimeFactor)
          .round(),
    );
    if (estimate < _minProcessingEstimate) return _minProcessingEstimate;
    if (estimate > _maxProcessingEstimate) return _maxProcessingEstimate;
    return estimate;
  }

  static String _megabytes(int bytes) {
    return (bytes / (1024 * 1024)).toStringAsFixed(1);
  }
}
