import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';

void main() {
  test('a sentence can belong to only one question', () {
    final exercises = LessonExercises.fromJson({
      'questions': [
        {
          'id': 'first',
          'title': 'First',
          'cueIndexes': [0, 1],
        },
        {
          'id': 'second',
          'title': 'Second',
          'cueIndexes': [1, 2],
        },
      ],
      'cloze': const {},
    });

    expect(exercises.questions[0].cueIndexes, [0, 1]);
    expect(exercises.questions[1].cueIndexes, [2]);
    expect(exercises.questionIndexForCue(1), 0);
  });

  test('punctuation is not emitted as a selectable word', () {
    final parts = tokenizeLessonText("Hello, don't stop!");
    final words = parts
        .where((part) => part.isWord)
        .map((part) => part.text)
        .toList();

    expect(words, ['Hello', "don't", 'stop']);
  });

  test('one material owns cues for multiple questions', () {
    final exercises = LessonExercises.fromJson({
      'materials': [
        {
          'id': 'material-6-7',
          'prompt': '回答第6和第7小题',
          'cueIndexes': [10, 11],
          'questionIds': ['q6', 'q7'],
        },
      ],
      'questions': [
        {'id': 'q6', 'number': 6, 'title': '第6题'},
        {'id': 'q7', 'number': 7, 'title': '第7题'},
      ],
      'cloze': const {},
    });

    expect(exercises.effectiveMaterials, hasLength(1));
    expect(exercises.questions, hasLength(2));
    expect(exercises.questions[0].cueIndexes, [10, 11]);
    expect(exercises.questions[1].cueIndexes, [10, 11]);
    expect(exercises.materialIndexForCue(10), 0);
  });

  test('keeps options, answer and lead-in across a project round trip', () {
    final exercises = LessonExercises.fromJson({
      'materials': [
        {
          'id': 'm1',
          'prompt': '回答第3题',
          'cueIndexes': [2, 3],
          'leadInCueIndexes': [1],
          'questionIds': ['q3'],
        },
      ],
      'questions': [
        {
          'id': 'q3',
          'number': 3,
          'title': 'What happened?',
          'options': ['First', 'Second', 'Third'],
          'answerIndex': 1,
        },
      ],
    });
    final restored = LessonExercises.fromJson(exercises.toJson());
    expect(restored.materialForCue(1)?.id, 'm1');
    expect(restored.questions.single.options, ['First', 'Second', 'Third']);
    expect(restored.questions.single.answerIndex, 1);
  });
}
