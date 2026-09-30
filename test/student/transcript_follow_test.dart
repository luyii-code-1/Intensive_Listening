import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
import 'package:intensive_listening/main.dart';
import 'package:scrollable_positioned_list/scrollable_positioned_list.dart';

void main() {
  final cues = List.generate(
    60,
    (index) => SrtCue(
      start: Duration(seconds: index * 2),
      end: Duration(seconds: index * 2 + 1),
      text: 'Sentence $index',
    ),
  );
  final exercises = LessonExercises(
    materials: [
      for (var group = 0; group < 6; group++)
        LessonMaterial(
          id: 'material-$group',
          prompt: '',
          cueIndexes: [
            for (var cue = group * 10; cue < group * 10 + 10; cue++) cue,
          ],
          questionIds: ['question-$group'],
        ),
    ],
    questions: [
      for (var group = 0; group < 6; group++)
        LessonQuestion(
          id: 'question-$group',
          title: 'Question $group',
          cueIndexes: [
            for (var cue = group * 10; cue < group * 10 + 10; cue++) cue,
          ],
          materialId: 'material-$group',
          number: group + 1,
        ),
    ],
  );

  Widget pane(int activeIndex, {bool showSubtitles = true}) => FluentApp(
    home: TranscriptPane(
      cues: cues,
      activeIndex: activeIndex,
      onSelected: (_) {},
      exercises: exercises,
      revealedCloze: const {},
      showAllCloze: false,
      showSubtitles: showSubtitles,
      onToggleCloze: (_, _) {},
      onShowAllCloze: (_) {},
      questionIndex: activeIndex ~/ 10,
      playing: true,
    ),
  );

  testWidgets('far question jump follows and resumes after user scrolling', (
    tester,
  ) async {
    await tester.pumpWidget(pane(0));
    await tester.pumpWidget(pane(50));
    await tester.pump(const Duration(seconds: 2));
    await tester.pump(const Duration(milliseconds: 450));
    await tester.pump(const Duration(milliseconds: 450));
    await tester.pump(const Duration(milliseconds: 350));
    expect(find.text(' 50'), findsOneWidget);

    await tester.drag(
      find.byType(ScrollablePositionedList),
      const Offset(0, 500),
    );
    await tester.pump(const Duration(seconds: 5));
    await tester.pump(const Duration(milliseconds: 450));
    await tester.pump(const Duration(milliseconds: 450));
    expect(find.text(' 50'), findsOneWidget);

    await tester.pumpWidget(pane(50, showSubtitles: false));
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text(' 50'), findsOneWidget);
  });

  testWidgets('playing transcript click seeks directly', (tester) async {
    final jumps = <int>[];
    await tester.pumpWidget(
      FluentApp(
        home: TranscriptPane(
          cues: cues.take(2).toList(),
          activeIndex: 0,
          onSelected: (_) {},
          exercises: const LessonExercises(),
          revealedCloze: const {},
          showAllCloze: false,
          showSubtitles: true,
          onToggleCloze: (_, _) {},
          onShowAllCloze: (_) {},
          questionIndex: -1,
          playing: true,
          onJumpCue: (index) async => jumps.add(index),
        ),
      ),
    );

    tester.widgetList<ListTile>(find.byType(ListTile)).last.onPressed!();
    await tester.pump();

    expect(jumps, [1]);
    expect(find.text('播放并暂停'), findsNothing);
  });

  testWidgets(
    'paused transcript click highlights one cue and offers playback',
    (tester) async {
      var activeIndex = 0;
      final playback = <bool>[];
      await tester.pumpWidget(
        FluentApp(
          home: StatefulBuilder(
            builder: (context, setState) => TranscriptPane(
              cues: cues.take(2).toList(),
              activeIndex: activeIndex,
              onSelected: (_) {},
              exercises: const LessonExercises(),
              revealedCloze: const {},
              showAllCloze: false,
              showSubtitles: true,
              onToggleCloze: (_, _) {},
              onShowAllCloze: (_) {},
              questionIndex: -1,
              playing: false,
              onAdoptPausedCue: (index) async =>
                  setState(() => activeIndex = index),
              onPlaySelected: (index, {required pauseAfterSentence}) async {
                playback.add(pauseAfterSentence);
              },
            ),
          ),
        ),
      );

      tester.widgetList<ListTile>(find.byType(ListTile)).last.onPressed!();
      await tester.pump();

      expect(activeIndex, 1);
      expect(
        tester
            .widgetList<ListTile>(find.byType(ListTile))
            .where((tile) => tile.selected)
            .length,
        1,
      );
      expect(find.text('播放'), findsOneWidget);
      expect(find.text('播放并暂停'), findsOneWidget);

      await tester.tap(find.text('播放并暂停'));
      await tester.pump(const Duration(milliseconds: 150));
      expect(playback, [true]);

      tester.widgetList<ListTile>(find.byType(ListTile)).first.onPressed!();
      await tester.pump();
      expect(activeIndex, 0);
      expect(
        tester
            .widgetList<ListTile>(find.byType(ListTile))
            .where((tile) => tile.selected)
            .length,
        1,
      );
      await tester.tap(find.text('播放'));
      await tester.pump(const Duration(milliseconds: 150));
      expect(playback, [true, false]);
    },
  );
}
