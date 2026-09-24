import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/srt_sections.dart';

void main() {
  SrtCue cue(String text, int second) => SrtCue(
    start: Duration(seconds: second),
    end: Duration(seconds: second + 1),
    text: text,
  );

  test('uses one continuous section when the SRT has no Text markers', () {
    final structure = SrtTranscriptStructure.fromCues([
      cue('First sentence.', 0),
      cue('Second sentence.', 1),
    ]);

    expect(structure.hasMarkers, isFalse);
    expect(structure.sections, hasLength(1));
    expect(structure.sections.single.label, '原文');
    expect(structure.sections.single.cueIndexes, [0, 1]);
  });

  test(
    'turns Text marker cues into sections without selectable marker rows',
    () {
      final structure = SrtTranscriptStructure.fromCues([
        cue('Preface', 0),
        cue('Text 6', 1),
        cue('Question one.', 2),
        cue('text XXX：', 3),
        cue('Question two.', 4),
      ]);

      expect(structure.hasMarkers, isTrue);
      expect(structure.markerCueIndexes, {1, 3});
      expect(structure.sections.map((section) => section.label), [
        '题前原文',
        'Text 6',
        'Text XXX',
      ]);
      expect(structure.sections.map((section) => section.cueIndexes), [
        [0],
        [2],
        [4],
      ]);
      expect(structure.sectionIndexForCue(2), 1);
      expect(structure.sectionIndexForCue(3), isNull);
    },
  );

  test('ignores empty sections created by consecutive markers', () {
    final structure = SrtTranscriptStructure.fromCues([
      cue('Text A', 0),
      cue('Text B', 1),
      cue('Only content.', 2),
    ]);

    expect(structure.sections, hasLength(1));
    expect(structure.sections.single.label, 'Text B');
    expect(structure.sections.single.cueIndexes, [2]);
  });

  test('keeps listening prompts inside the original transcript flow', () {
    final structure = SrtTranscriptStructure.fromCues([
      cue('听下面的对话，回答第 1 至 2 题。', 0),
      cue('Good morning.', 1),
      cue('听下一面的材料，完成下面的问题。', 2),
      cue('Welcome to our program.', 3),
      cue('请听第八段\n短文，然后回答下面三道题。', 4),
      cue('The final passage.', 5),
    ]);

    expect(structure.markerCueIndexes, isEmpty);
    expect(structure.sections, hasLength(1));
    expect(structure.sections.single.label, '原文');
    expect(structure.sections.single.cueIndexes, [0, 1, 2, 3, 4, 5]);
  });

  test('manual mode keeps prompt and Text cues editable', () {
    final structure = SrtTranscriptStructure.fromCues([
      cue('Text 1', 0),
      cue('Dialogue.', 1),
    ], automatic: false);

    expect(structure.markerCueIndexes, isEmpty);
    expect(structure.sections.single.cueIndexes, [0, 1]);
  });
}
