import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import '../audio/vad_slicer.dart';
import '../cancellation.dart';
import '../ilp/ilp_models.dart';
import '../ilp/srt_parser.dart';
import 'asr_client.dart';

class SegmentedAsrRunner {
  const SegmentedAsrRunner({
    required this.config,
    required this.concurrency,
    required this.timeout,
    this.slicer = const VadSlicer(),
  });

  final AsrConfig config;
  final int concurrency;
  final Duration timeout;
  final VadSlicer slicer;

  static const maxRequestsPerSecond = 10;

  Future<String> call({
    required File audioFile,
    AsrProgressCallback? onProgress,
    Future<void>? abortTrigger,
    Duration? estimatedProcessingTime,
    Future<bool> Function()? confirmForcedCuts,
  }) async {
    final tempDirectory = await Directory.systemTemp.createTemp('ilp-asr-');
    try {
      onProgress?.call(const AsrProgress(stage: AsrStage.decoding));
      onProgress?.call(const AsrProgress(stage: AsrStage.slicing));
      VadSliceResult sliced;
      try {
        sliced = await slicer.slice(
          audioFile,
          tempDirectory,
          maxSegmentDuration: const Duration(seconds: 120),
          abortTrigger: abortTrigger,
        );
      } on NoSilenceCutException {
        if (confirmForcedCuts == null || !await confirmForcedCuts()) {
          throw const OperationCanceled();
        }
        sliced = await slicer.slice(
          audioFile,
          tempDirectory,
          maxSegmentDuration: const Duration(seconds: 120),
          forceCutOnNoSilence: true,
          abortTrigger: abortTrigger,
        );
      }
      final slices = sliced.slices;
      final results = List<List<SrtCue>?>.filled(slices.length, null);
      var nextIndex = 0;
      var completed = 0;
      final requestLimiter = _RequestStartLimiter(maxRequestsPerSecond);

      Future<void> worker() async {
        while (true) {
          if (nextIndex >= slices.length) return;
          final index = nextIndex++;
          final slice = slices[index];
          await requestLimiter.acquire();
          final srt = await AsrClient(timeout: timeout).transcribeToSrt(
            config: config,
            audioFile: slice.file,
            abortTrigger: abortTrigger,
          );
          final localCues = srt.trim().isEmpty
              ? const <SrtCue>[]
              : SrtParser.parse(
                  utf8.encode(srt),
                  slice.end - slice.start + const Duration(seconds: 1),
                );
          results[index] = [
            for (final cue in localCues)
              SrtCue(
                start: cue.start + slice.start,
                end: cue.end + slice.start,
                text: cue.text,
              ),
          ];
          completed += 1;
          onProgress?.call(
            AsrProgress(
              stage: AsrStage.recognizing,
              stageFraction: completed / slices.length,
              segmentIndex: completed,
              segmentTotal: slices.length,
            ),
          );
        }
      }

      final workerCount = math.min(
        concurrency.clamp(1, maxRequestsPerSecond),
        slices.length,
      );
      await Future.wait(
        List.generate(workerCount, (_) => worker()),
        eagerError: true,
      );
      onProgress?.call(
        AsrProgress(
          stage: AsrStage.merging,
          segmentIndex: slices.length,
          segmentTotal: slices.length,
        ),
      );
      final merged = _normalize(
        results.whereType<List<SrtCue>>().expand((cues) => cues),
      );
      return AsrTranscription(merged).toSrt();
    } finally {
      if (await tempDirectory.exists()) {
        await tempDirectory.delete(recursive: true);
      }
    }
  }

  static List<SrtCue> _normalize(Iterable<SrtCue> source) {
    final sorted = source.toList()
      ..sort((left, right) => left.start.compareTo(right.start));
    final result = <SrtCue>[];
    for (final cue in sorted) {
      final start = result.isEmpty || cue.start >= result.last.end
          ? cue.start
          : result.last.end;
      if (cue.end <= start || cue.text.trim().isEmpty) continue;
      result.add(SrtCue(start: start, end: cue.end, text: cue.text.trim()));
    }
    return List.unmodifiable(result);
  }
}

class _RequestStartLimiter {
  _RequestStartLimiter(int requestsPerSecond)
    : _interval = Duration(
        microseconds: (Duration.microsecondsPerSecond / requestsPerSecond)
            .ceil(),
      );

  final Duration _interval;
  Future<void> _tail = Future<void>.value();
  DateTime _nextStart = DateTime.fromMillisecondsSinceEpoch(0);

  Future<void> acquire() {
    final completer = Completer<void>();
    _tail = _tail.then((_) async {
      final now = DateTime.now();
      final delay = _nextStart.difference(now);
      if (delay > Duration.zero) await Future<void>.delayed(delay);
      _nextStart = DateTime.now().add(_interval);
      completer.complete();
    });
    return completer.future;
  }
}
