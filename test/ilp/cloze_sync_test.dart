import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/cloze_sync.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';

void main() {
  const cues = [
    SrtCue(
      start: Duration(seconds: 1),
      end: Duration(seconds: 2),
      text: 'The same way when I first read it, the more I thought.',
    ),
    SrtCue(
      start: Duration(seconds: 3),
      end: Duration(seconds: 4),
      text: 'I felt the same way when I first read it, the more I thought.',
    ),
  ];
  const exercises = LessonExercises(
    questions: [
      LessonQuestion(
        id: 'question-1',
        title: 'Question 1',
        cueIndexes: [0, 1],
        repeatedCueIndexes: [1],
      ),
    ],
  );

  test('mirrors cloze changes across aligned repeated words', () {
    final selected = toggleSynchronizedClozeWord(
      current: const {},
      cues: cues,
      exercises: exercises,
      cueIndex: 0,
      wordIndex: 11,
    );

    expect(selected[0], {11});
    expect(selected[1], {13});

    final cleared = toggleSynchronizedClozeWord(
      current: selected,
      cues: cues,
      exercises: exercises,
      cueIndex: 1,
      wordIndex: 13,
    );
    expect(cleared, isEmpty);
  });

  test('keeps an unpaired cloze change local', () {
    final selected = toggleSynchronizedClozeWord(
      current: const {},
      cues: cues,
      exercises: const LessonExercises(),
      cueIndex: 0,
      wordIndex: 2,
    );

    expect(selected, {
      0: {2},
    });
  });
}
