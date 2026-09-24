import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/audio/vad_slicer.dart';

void main() {
  test('parses ffmpeg duration and silence ranges', () {
    const output = '''
Duration: 00:04:10.000, start: 0.000000, bitrate: 128 kb/s
[silencedetect] silence_start: 99.8
[silencedetect] silence_end: 100.6 | silence_duration: 0.8
[silencedetect] silence_start: 199.7
[silencedetect] silence_end: 200.5 | silence_duration: 0.8
''';
    final duration = parseFfmpegDuration(output)!;
    final ranges = parseSilenceRanges(output, duration);

    expect(duration, const Duration(seconds: 250));
    expect(ranges, hasLength(2));
    expect(ranges.first.midpoint, const Duration(milliseconds: 100200));
  });

  test(
    'selects only silence cuts and keeps every segment within 120 seconds',
    () {
      final plan = chooseVadCutPoints(const Duration(seconds: 250), const [
        SilenceRange(Duration(seconds: 99), Duration(seconds: 101)),
        SilenceRange(Duration(seconds: 199), Duration(seconds: 201)),
      ]);

      expect(plan.cutPoints, const [
        Duration(seconds: 100),
        Duration(seconds: 200),
      ]);
    },
  );

  test('asks before a forced cut when no pause exists inside 120 seconds', () {
    expect(
      () => chooseVadCutPoints(const Duration(seconds: 121), const []),
      throwsA(isA<NoSilenceCutException>()),
    );
    final forced = chooseVadCutPoints(
      const Duration(seconds: 121),
      const [],
      forceCutOnNoSilence: true,
    );
    expect(forced.cutPoints, const [Duration(seconds: 120)]);
  });

  test('uses the 120 second boundary when a silence straddles it', () {
    final plan = chooseVadCutPoints(const Duration(seconds: 121), const [
      SilenceRange(
        Duration(milliseconds: 119800),
        Duration(milliseconds: 120400),
      ),
    ]);

    expect(plan.cutPoints, const [Duration(seconds: 120)]);
  });
}
