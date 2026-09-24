import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;

import '../cancellation.dart';
import 'ffmpeg_locator.dart';

class SilenceRange {
  const SilenceRange(this.start, this.end);

  final Duration start;
  final Duration end;

  Duration get midpoint =>
      Duration(microseconds: (start.inMicroseconds + end.inMicroseconds) ~/ 2);
}

class VadSlice {
  const VadSlice({required this.file, required this.start, required this.end});

  final File file;
  final Duration start;
  final Duration end;
}

class VadSliceResult {
  const VadSliceResult({required this.slices, required this.duration});

  final List<VadSlice> slices;
  final Duration duration;
}

class VadSlicer {
  const VadSlicer({this.ffmpegPath});

  final String? ffmpegPath;

  Future<VadSliceResult> slice(
    File input,
    Directory outputDirectory, {
    Duration maxSegmentDuration = const Duration(seconds: 120),
    bool forceCutOnNoSilence = false,
    Future<void>? abortTrigger,
  }) async {
    final executable = ffmpegPath ?? locateFfmpeg();
    if (!await File(executable).exists()) {
      throw const FileSystemException('缺少 ffmpeg.exe，请运行一键编译脚本补齐运行组件。');
    }
    await outputDirectory.create(recursive: true);

    final analysis = await _runFfmpeg(executable, [
      '-hide_banner',
      '-nostats',
      '-i',
      input.path,
      '-af',
      'silencedetect=noise=-35dB:d=0.25',
      '-f',
      'null',
      '-',
    ], abortTrigger: abortTrigger);
    final duration = parseFfmpegDuration(analysis.stderrText);
    if (duration == null || duration <= Duration.zero) {
      throw const FormatException('无法读取音频时长。');
    }
    final silences = parseSilenceRanges(analysis.stderrText, duration);
    final plan = chooseVadCutPoints(
      duration,
      silences,
      maxSegmentDuration: maxSegmentDuration,
      forceCutOnNoSilence: forceCutOnNoSilence,
    );
    final outputPattern = p.join(outputDirectory.path, 'chunk_%03d.wav');
    final arguments = <String>[
      '-hide_banner',
      '-nostats',
      '-loglevel',
      'error',
      '-i',
      input.path,
      if (plan.cutPoints.isNotEmpty) ...[
        '-f',
        'segment',
        '-segment_times',
        plan.cutPoints
            .map((value) => (value.inMilliseconds / 1000).toStringAsFixed(3))
            .join(','),
        '-reset_timestamps',
        '1',
      ],
      '-ac',
      '1',
      '-ar',
      '16000',
      '-c:a',
      'pcm_s16le',
      outputPattern,
    ];
    await _runFfmpeg(executable, arguments, abortTrigger: abortTrigger);

    final files =
        outputDirectory
            .listSync()
            .whereType<File>()
            .where((file) => p.basename(file.path).startsWith('chunk_'))
            .toList()
          ..sort((left, right) => left.path.compareTo(right.path));
    final boundaries = [Duration.zero, ...plan.cutPoints, duration];
    if (files.length != boundaries.length - 1) {
      throw const FormatException('音频切片数量与时间边界不一致。');
    }
    return VadSliceResult(
      slices: [
        for (var index = 0; index < files.length; index++)
          VadSlice(
            file: files[index],
            start: boundaries[index],
            end: boundaries[index + 1],
          ),
      ],
      duration: duration,
    );
  }
}

class VadCutPlan {
  const VadCutPlan(this.cutPoints);

  final List<Duration> cutPoints;
}

class NoSilenceCutException implements Exception {
  const NoSilenceCutException();

  @override
  String toString() => '120 秒范围内未检测到静音，可选择强制切片继续转写。';
}

VadCutPlan chooseVadCutPoints(
  Duration duration,
  List<SilenceRange> silences, {
  Duration maxSegmentDuration = const Duration(seconds: 120),
  Duration minSegmentDuration = const Duration(seconds: 15),
  bool forceCutOnNoSilence = false,
}) {
  final orderedSilences = [...silences]
    ..sort((left, right) => left.start.compareTo(right.start));
  final cuts = <Duration>[];
  var cursor = Duration.zero;
  while (duration - cursor > maxSegmentDuration) {
    final minimum = cursor + minSegmentDuration;
    final maximum = cursor + maxSegmentDuration;
    Duration? selected;
    for (final silence in orderedSilences) {
      if (silence.end < minimum) continue;
      if (silence.start > maximum) break;
      final midpoint = silence.midpoint;
      final candidate = midpoint > maximum
          ? maximum
          : midpoint < minimum
          ? minimum
          : midpoint;
      if (candidate >= silence.start && candidate <= silence.end) {
        selected = candidate;
      }
    }
    if (selected == null) {
      if (!forceCutOnNoSilence) throw const NoSilenceCutException();
      selected = maximum;
    }
    cuts.add(selected);
    cursor = selected;
  }
  return VadCutPlan(List.unmodifiable(cuts));
}

Duration? parseFfmpegDuration(String output) {
  final match = RegExp(r'Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)')
      .firstMatch(output);
  if (match == null) return null;
  final hours = int.parse(match.group(1)!);
  final minutes = int.parse(match.group(2)!);
  final seconds = double.parse(match.group(3)!);
  return Duration(
    milliseconds: (((hours * 60 + minutes) * 60 + seconds) * 1000).round(),
  );
}

List<SilenceRange> parseSilenceRanges(String output, Duration duration) {
  final eventPattern = RegExp(r'silence_(start|end):\s*(-?\d+(?:\.\d+)?)');
  final ranges = <SilenceRange>[];
  Duration? start;
  for (final match in eventPattern.allMatches(output)) {
    final value = Duration(
      microseconds: (double.parse(match.group(2)!) * 1000000).round(),
    );
    if (match.group(1) == 'start') {
      start = value < Duration.zero ? Duration.zero : value;
    } else if (start != null && value > start) {
      ranges.add(SilenceRange(start, value > duration ? duration : value));
      start = null;
    }
  }
  if (start != null && start < duration) {
    ranges.add(SilenceRange(start, duration));
  }
  return ranges;
}

class _FfmpegResult {
  const _FfmpegResult(this.stderrText);

  final String stderrText;
}

Future<_FfmpegResult> _runFfmpeg(
  String executable,
  List<String> arguments, {
  Future<void>? abortTrigger,
}) async {
  final process = await Process.start(executable, arguments, runInShell: false);
  var canceled = false;
  if (abortTrigger != null) {
    unawaited(
      abortTrigger.then((_) {
        canceled = true;
        process.kill();
      }),
    );
  }
  final stderrFuture = process.stderr.transform(utf8.decoder).join();
  final stdoutFuture = process.stdout.drain<void>();
  final exitCode = await process.exitCode;
  final stderrText = await stderrFuture;
  await stdoutFuture;
  if (canceled) throw const OperationCanceled();
  if (exitCode != 0) {
    final lastLine = stderrText.trim().split('\n').lastOrNull?.trim();
    throw FormatException(
      lastLine?.isNotEmpty == true ? lastLine! : 'ffmpeg 执行失败。',
    );
  }
  return _FfmpegResult(stderrText);
}
