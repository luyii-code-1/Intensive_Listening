import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
import 'package:intensive_listening/main.dart';

void main() {
  const cues = [
    SrtCue(
      start: Duration(seconds: 1),
      end: Duration(seconds: 2),
      text: 'First',
    ),
    SrtCue(
      start: Duration(seconds: 3),
      end: Duration(seconds: 4),
      text: 'Second',
    ),
  ];

  test('keeps the finished sentence active through the following gap', () {
    expect(activeCueIndexForPosition(cues, Duration.zero), 0);
    expect(activeCueIndexForPosition(cues, const Duration(seconds: 2)), 0);
    expect(
      activeCueIndexForPosition(cues, const Duration(milliseconds: 2500)),
      0,
    );
    expect(activeCueIndexForPosition(cues, const Duration(seconds: 3)), 1);
  });

  test('returns no active sentence when a lesson has no cues', () {
    expect(activeCueIndexForPosition(const [], Duration.zero), -1);
  });

  test('cue jumps land 50 milliseconds inside the selected sentence', () {
    expect(cueSeekPosition(cues.first), const Duration(milliseconds: 1050));
    expect(
      cueSeekPosition(
        const SrtCue(
          start: Duration(seconds: 5),
          end: Duration(milliseconds: 5020),
          text: 'Short',
        ),
      ),
      const Duration(seconds: 5),
    );
  });

  testWidgets('playback slider uses a normalized range for seeking', (
    tester,
  ) async {
    Duration? requestedPosition;
    await tester.pumpWidget(
      FluentApp(
        home: PlaybackControls(
          duration: const Duration(seconds: 20),
          position: const Duration(seconds: 10),
          playing: false,
          onTogglePlayback: () async {},
          onSeek: (position) async => requestedPosition = position,
        ),
      ),
    );

    final slider = tester.widget<Slider>(find.byType(Slider));
    expect(slider.min, 0);
    expect(slider.max, 1);
    expect(slider.value, 0.5);

    slider.onChanged!(0.25);
    await tester.pump();
    expect(requestedPosition, const Duration(seconds: 5));
  });

  testWidgets('single sentence loop uses a Fluent checkbox', (tester) async {
    bool? enabled;
    await tester.pumpWidget(
      FluentApp(
        home: PlaybackControls(
          duration: const Duration(seconds: 20),
          position: const Duration(seconds: 10),
          playing: false,
          onTogglePlayback: () async {},
          onSeek: (_) async {},
          onSingleSentenceLoopChanged: (value) => enabled = value,
        ),
      ),
    );

    expect(find.text('单句循环'), findsOneWidget);
    expect(find.text('重播本句'), findsNothing);
    await tester.tap(find.byType(Checkbox));
    await tester.pump(const Duration(milliseconds: 150));
    expect(enabled, isTrue);
  });

  testWidgets('playback controls expose a Fluent hide-subtitles checkbox', (
    tester,
  ) async {
    bool? visible;
    await tester.pumpWidget(
      FluentApp(
        home: PlaybackControls(
          duration: const Duration(seconds: 20),
          position: const Duration(seconds: 10),
          playing: false,
          onTogglePlayback: () async {},
          onSeek: (_) async {},
          showSubtitles: true,
          onShowSubtitlesChanged: (value) => visible = value,
        ),
      ),
    );

    expect(find.text('隐藏字幕'), findsOneWidget);
    await tester.tap(find.widgetWithText(Checkbox, '隐藏字幕'));
    await tester.pump(const Duration(milliseconds: 150));
    expect(visible, isFalse);
  });

  testWidgets('playback has visible sentence and question controls', (
    tester,
  ) async {
    final sentenceSteps = <int>[];
    final questionSteps = <int>[];
    await tester.pumpWidget(
      FluentApp(
        home: PlaybackControls(
          duration: const Duration(seconds: 20),
          position: const Duration(seconds: 10),
          playing: false,
          onTogglePlayback: () async {},
          onSeek: (_) async {},
          onStepSentence: (delta) async => sentenceSteps.add(delta),
          onStepQuestion: (delta) async => questionSteps.add(delta),
        ),
      ),
    );

    expect(find.widgetWithText(Button, '上一句'), findsOneWidget);
    expect(find.byIcon(FluentIcons.play_solid), findsOneWidget);
    expect(find.widgetWithText(Button, '下一句'), findsOneWidget);
    for (final label in const ['上一题', '下一题']) {
      expect(find.widgetWithText(Button, label), findsOneWidget);
    }
    await tester.tap(find.text('下一句'));
    await tester.tap(find.text('上一题'));
    await tester.pump(const Duration(milliseconds: 150));
    expect(sentenceSteps, [1]);
    expect(questionSteps, [-1]);
  });

  testWidgets('player focal area shows the current question', (tester) async {
    await tester.pumpWidget(
      FluentApp(
        home: PlayerSection(
          hasTranscript: true,
          questions: const [
            LessonQuestion(
              id: 'question-1',
              number: 1,
              title: 'What did the speaker decide?',
              cueIndexes: [0],
            ),
          ],
          showSubtitles: true,
          duration: const Duration(seconds: 20),
          position: const Duration(seconds: 5),
          playing: false,
          onTogglePlayback: () async {},
          onSeek: (_) async {},
          onStepSentence: (_) async {},
          onSingleSentenceLoopChanged: (_) {},
          onShowSubtitlesChanged: (_) {},
        ),
      ),
    );

    expect(find.text('第 1 题  What did the speaker decide?'), findsOneWidget);
    expect(find.byIcon(FluentIcons.music_note), findsNothing);
  });
}
