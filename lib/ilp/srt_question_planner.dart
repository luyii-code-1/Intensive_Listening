import 'ilp_models.dart';
import 'lesson_exercises.dart';

/// Builds an initial question plan from an ASR transcript.
///
/// The planner keeps narration outside questions. When it detects two
/// consecutive readings of the same passage, both time ranges stay in one
/// material while the second range is marked for compact presentation.
class SrtQuestionPlanner {
  const SrtQuestionPlanner();

  static final RegExp _explicitMarker = RegExp(
    r'^Text\s+[A-Za-z0-9_-]+\s*[.。:：-]?\s*$',
    caseSensitive: false,
  );
  static final RegExp _compactMarker = RegExp(
    r'^[Tt]ext(?:\d+|[A-Z]{1,4})\s*[.。:：-]?\s*$',
  );
  static final RegExp _leadingMarker = RegExp(
    r'^\s*(?:[Tt]ext\s+[A-Za-z0-9_-]+|[Tt]ext(?:\d+|[A-Z]{1,4}))\s*[:：.。,-]?\s*',
  );
  static final RegExp _englishInstruction = RegExp(
    r'\b(?:listen|you\s+will\s+hear|answer|choose|question|section)\b',
    caseSensitive: false,
  );
  static final RegExp _questionPrompt = RegExp(
    r'(?:回答|完成|选择).{0,36}(?:小?题|个问题)|'
    r'(?:听|listen|hear).{0,48}(?:回答|answer|question)',
    caseSensitive: false,
    unicode: true,
  );
  static final RegExp _hanText = RegExp(r'[\u3400-\u9fff]');
  static final RegExp _latinWord = RegExp(r"[A-Za-z]+(?:['’-][A-Za-z]+)*");
  static final RegExp _numberOnly = RegExp(r'^[\d\sA-Ca-c.,，。:：-]+$');
  static final RegExp _questionNumberToken = RegExp(
    r'\d{1,3}|[零〇一二两三四五六七八九十百]{1,5}',
  );

  LessonExercises plan(
    List<SrtCue> cues, {
    Map<int, Set<int>> clozeWordIndexes = const {},
  }) {
    if (cues.isEmpty) {
      return LessonExercises(clozeWordIndexes: clozeWordIndexes);
    }

    final runs = _contentRuns(cues);
    final materials = <LessonMaterial>[];
    final questions = <LessonQuestion>[];
    var nextQuestionNumber = 1;
    for (final run in runs) {
      final repetitionStart = _repetitionStart(cues, run.cueIndexes);
      if (repetitionStart == null && !run.followsQuestionPrompt) continue;

      final repeatedCueIndexes = repetitionStart == null
          ? const <int>[]
          : run.cueIndexes.sublist(repetitionStart);
      final materialId =
          'material-${run.cueIndexes.first}-${run.cueIndexes.last}';
      final parsedNumbers = run.questionNumbers;
      final numbers = parsedNumbers.isEmpty
          ? [nextQuestionNumber]
          : parsedNumbers;
      final questionIds = <String>[];
      for (final number in numbers) {
        final questionId = 'auto-$number-${run.cueIndexes.first}';
        questionIds.add(questionId);
        questions.add(
          LessonQuestion(
            id: questionId,
            title: '第 $number 题',
            number: number,
            materialId: materialId,
            cueIndexes: List.unmodifiable(run.cueIndexes),
            repeatedCueIndexes: List.unmodifiable(repeatedCueIndexes),
          ),
        );
      }
      nextQuestionNumber = numbers.reduce((a, b) => a > b ? a : b) + 1;
      materials.add(
        LessonMaterial(
          id: materialId,
          prompt: run.prompt,
          cueIndexes: List.unmodifiable(run.cueIndexes),
          repeatedCueIndexes: List.unmodifiable(repeatedCueIndexes),
          questionIds: List.unmodifiable(questionIds),
        ),
      );
    }

    return LessonExercises(
      materials: List.unmodifiable(materials),
      questions: List.unmodifiable(questions),
      clozeWordIndexes: clozeWordIndexes,
    );
  }

  List<_ContentRun> _contentRuns(List<SrtCue> cues) {
    final result = <_ContentRun>[];
    var indexes = <int>[];
    var followsQuestionPrompt = false;
    var prompt = '';
    var questionNumbers = <int>[];

    void flush() {
      if (indexes.isEmpty) return;
      result.add(
        _ContentRun(
          cueIndexes: List.unmodifiable(indexes),
          followsQuestionPrompt: followsQuestionPrompt,
          prompt: prompt,
          questionNumbers: List.unmodifiable(questionNumbers),
        ),
      );
      indexes = <int>[];
      followsQuestionPrompt = false;
      prompt = '';
      questionNumbers = <int>[];
    }

    for (var index = 0; index < cues.length; index++) {
      final cue = cues[index];
      final text = cue.text
          .replaceFirst(_leadingMarker, '')
          .replaceAll(RegExp(r'\s+'), ' ')
          .trim();
      final narration = _isNarration(cue, text);
      if (narration) {
        flush();
        final isQuestionPrompt =
            _questionPrompt.hasMatch(text) ||
            (_numberOnly.hasMatch(text) && text.length <= 16);
        followsQuestionPrompt = followsQuestionPrompt || isQuestionPrompt;
        if (isQuestionPrompt) {
          prompt = text;
          questionNumbers = _questionNumbers(text);
        }
        continue;
      }

      if (indexes.isNotEmpty) {
        final previous = cues[indexes.last];
        if (cue.start - previous.end > const Duration(seconds: 12)) flush();
      }
      indexes.add(index);
    }
    flush();
    return result;
  }

  List<int> _questionNumbers(String text) {
    var normalized = _normalizeQuestionDigits(text);
    final answerMatches = RegExp(
      r'回答|answer',
      caseSensitive: false,
    ).allMatches(normalized).toList();
    if (answerMatches.isNotEmpty) {
      normalized = normalized.substring(answerMatches.last.end);
    }
    final ordinalStart = normalized.indexOf('第');
    if (ordinalStart >= 0) {
      normalized = normalized.substring(ordinalStart + 1);
    }
    final firstToken = _questionNumberToken.firstMatch(normalized);
    if (firstToken == null) return const [];
    final questionSuffix = RegExp(r'小?题')
        .firstMatch(normalized.substring(firstToken.start));
    if (questionSuffix != null) {
      normalized = normalized.substring(
        firstToken.start,
        firstToken.start + questionSuffix.end,
      );
    } else {
      normalized = normalized.substring(firstToken.start);
    }

    final result = <int>[];
    for (final match in _questionNumberToken.allMatches(normalized)) {
      final value = _parseQuestionNumber(match.group(0)!);
      if (value != null && value > 0 && !result.contains(value)) {
        result.add(value);
      }
    }
    if (result.length == 2 && RegExp(r'(?:至|到|[-–—~～])').hasMatch(normalized)) {
      final start = result.first;
      final end = result.last;
      if (end > start && end - start <= 20) {
        return List<int>.generate(end - start + 1, (index) => start + index);
      }
    }
    return result;
  }

  String _normalizeQuestionDigits(String text) {
    const fullWidthDigits = '０１２３４５６７８９';
    var normalized = text.replaceAllMapped(RegExp(r'[０-９]'), (match) {
      return '${fullWidthDigits.indexOf(match.group(0)!)}';
    });
    normalized = normalized.replaceAllMapped(
      RegExp(r'\d(?:\s+\d)+'),
      (match) => match.group(0)!.replaceAll(RegExp(r'\s+'), ''),
    );
    return normalized;
  }

  int? _parseQuestionNumber(String token) {
    final arabic = int.tryParse(token);
    if (arabic != null) return arabic;
    const digits = <String, int>{
      '零': 0,
      '〇': 0,
      '一': 1,
      '二': 2,
      '两': 2,
      '三': 3,
      '四': 4,
      '五': 5,
      '六': 6,
      '七': 7,
      '八': 8,
      '九': 9,
    };
    if (!token.contains('十') && !token.contains('百')) {
      var value = 0;
      for (final character in token.split('')) {
        final digit = digits[character];
        if (digit == null) return null;
        value = value * 10 + digit;
      }
      return value;
    }
    var total = 0;
    var current = 0;
    for (final character in token.split('')) {
      final digit = digits[character];
      if (digit != null) {
        current = digit;
        continue;
      }
      final unit = character == '百'
          ? 100
          : character == '十'
          ? 10
          : null;
      if (unit == null) return null;
      total += (current == 0 ? 1 : current) * unit;
      current = 0;
    }
    return total + current;
  }

  bool _isNarration(SrtCue cue, String text) {
    if (text.isEmpty ||
        _explicitMarker.hasMatch(text) ||
        _compactMarker.hasMatch(text)) {
      return true;
    }
    if (_hanText.hasMatch(text)) return true;
    if (_englishInstruction.hasMatch(text) && _questionPrompt.hasMatch(text)) {
      return true;
    }
    if (_numberOnly.hasMatch(text) &&
        cue.end - cue.start >= const Duration(milliseconds: 1400)) {
      return true;
    }
    return !_latinWord.hasMatch(text) &&
        cue.end - cue.start >= const Duration(milliseconds: 1400);
  }

  int? _repetitionStart(List<SrtCue> cues, List<int> cueIndexes) {
    if (cueIndexes.length < 2) return null;
    var bestScore = 0.0;
    int? bestSplit;

    for (var split = 1; split < cueIndexes.length; split++) {
      final left = cueIndexes.sublist(0, split);
      final right = cueIndexes.sublist(split);
      final leftTokens = _tokens(cues, left);
      final rightTokens = _tokens(cues, right);
      if (leftTokens.length < 3 || rightTokens.length < 3) continue;

      final tokenRatio = leftTokens.length / rightTokens.length;
      if (tokenRatio < 0.55 || tokenRatio > 1.82) continue;
      final leftDuration = cues[left.last].end - cues[left.first].start;
      final rightDuration = cues[right.last].end - cues[right.first].start;
      if (leftDuration <= Duration.zero || rightDuration <= Duration.zero) {
        continue;
      }
      final durationRatio =
          leftDuration.inMilliseconds / rightDuration.inMilliseconds;
      if (durationRatio < 0.55 || durationRatio > 1.82) continue;

      final similarity = _multisetDice(leftTokens, rightTokens);
      final gap = cues[right.first].start - cues[left.last].end;
      final gapBonus = (gap.inMilliseconds / 5000).clamp(0.0, 0.08);
      final score = similarity + gapBonus;
      if (similarity >= 0.76 && score > bestScore) {
        bestScore = score;
        bestSplit = split;
      }
    }
    return bestSplit;
  }

  List<String> _tokens(List<SrtCue> cues, List<int> cueIndexes) {
    return [
      for (final cueIndex in cueIndexes)
        for (final match in _latinWord.allMatches(
          cues[cueIndex].text.replaceFirst(_leadingMarker, ''),
        ))
          match.group(0)!.toLowerCase().replaceAll(RegExp(r"['’-]"), ''),
    ];
  }

  double _multisetDice(List<String> left, List<String> right) {
    final counts = <String, int>{};
    for (final token in left) {
      counts[token] = (counts[token] ?? 0) + 1;
    }
    var intersection = 0;
    for (final token in right) {
      final available = counts[token] ?? 0;
      if (available == 0) continue;
      intersection += 1;
      counts[token] = available - 1;
    }
    return (2 * intersection) / (left.length + right.length);
  }
}

class _ContentRun {
  const _ContentRun({
    required this.cueIndexes,
    required this.followsQuestionPrompt,
    required this.prompt,
    required this.questionNumbers,
  });

  final List<int> cueIndexes;
  final bool followsQuestionPrompt;
  final String prompt;
  final List<int> questionNumbers;
}
