import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/srt_question_planner.dart';

void main() {
  test('keeps mixed-language narration and TextXXX outside the question', () {
    SrtCue cue(String text, int second) => SrtCue(
      start: Duration(seconds: second),
      end: Duration(seconds: second + 2),
      text: text,
    );

    final result = const SrtQuestionPlanner().plan([
      cue('听下面的对话，回答第 1 题。', 0),
      cue('TextXXX', 2),
      cue('TextXXX Hello there.', 4),
      cue('How are you?', 6),
    ]);

    expect(result.questions, hasLength(1));
    expect(result.questions.single.cueIndexes, [2, 3]);
  });

  test('creates two questions under one material from a shared prompt', () {
    SrtCue cue(String text, int second) => SrtCue(
      start: Duration(seconds: second),
      end: Duration(seconds: second + 2),
      text: text,
    );

    final result = const SrtQuestionPlanner().plan([
      cue('听下面的录音，回答第6和第7小题。', 0),
      cue('Where are they going?', 2),
      cue('They are going to school.', 4),
    ]);

    expect(result.effectiveMaterials, hasLength(1));
    expect(result.questions, hasLength(2));
    expect(result.questions.map((question) => question.number), [6, 7]);
    expect(result.effectiveMaterials.single.questionIds, hasLength(2));
    expect(result.effectiveMaterials.single.cueIndexes, [1, 2]);
    expect(result.questions[0].materialId, result.questions[1].materialId);
  });

  test('normalizes spaced, full-width and Chinese question numbers', () {
    SrtCue cue(String text, int second) => SrtCue(
      start: Duration(seconds: second),
      end: Duration(seconds: second + 2),
      text: text,
    );

    final spaced = const SrtQuestionPlanner().plan([
      cue('听下面的录音，回答第1 2 - 1 3题。', 0),
      cue('Where did she go?', 2),
    ]);
    final fullWidth = const SrtQuestionPlanner().plan([
      cue('听下面的录音，回答第１４至１５题。', 0),
      cue('What happened next?', 2),
    ]);
    final spacedRange = const SrtQuestionPlanner().plan([
      cue('听下面的录音，回答第1 4~1 7小题。', 0),
      cue('Where will they meet?', 2),
    ]);
    final chinese = const SrtQuestionPlanner().plan([
      cue('听下面的录音，回答第十六和第十七小题。', 0),
      cue('What will they do?', 2),
    ]);

    expect(spaced.questions.map((question) => question.number), [12, 13]);
    expect(fullWidth.questions.map((question) => question.number), [14, 15]);
    expect(spacedRange.questions.map((question) => question.number), [
      14,
      15,
      16,
      17,
    ]);
    expect(chinese.questions.map((question) => question.number), [16, 17]);
  });
}
