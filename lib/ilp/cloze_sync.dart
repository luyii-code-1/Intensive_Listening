import 'ilp_models.dart';
import 'lesson_exercises.dart';

/// Toggles one cloze word and mirrors the result to its aligned word in a
/// repeated reading of the same listening material.
Map<int, Set<int>> toggleSynchronizedClozeWord({
  required Map<int, Set<int>> current,
  required List<SrtCue> cues,
  required LessonExercises exercises,
  required int cueIndex,
  required int wordIndex,
}) {
  final updated = {
    for (final entry in current.entries) entry.key: {...entry.value},
  };
  if (cueIndex < 0 || cueIndex >= cues.length || wordIndex < 0) {
    return updated;
  }

  final selected = !(updated[cueIndex]?.contains(wordIndex) ?? false);
  final targets = <_WordLocation>{
    _WordLocation(cueIndex: cueIndex, wordIndex: wordIndex),
  };
  final material = exercises.materialForCue(cueIndex);
  if (material != null && material.repeatedCueIndexes.isNotEmpty) {
    final repeatedIndexes = material.repeatedCueIndexes.toSet();
    final primaryWords = _flattenWords(
      cues,
      material.cueIndexes.where((index) => !repeatedIndexes.contains(index)),
    );
    final repeatedWords = _flattenWords(cues, material.repeatedCueIndexes);
    final alignment = _alignRepeatedWords(primaryWords, repeatedWords);
    final selectedLocation = _WordLocation(
      cueIndex: cueIndex,
      wordIndex: wordIndex,
    );
    final counterpart = alignment[selectedLocation];
    if (counterpart != null) targets.add(counterpart);
  }

  for (final target in targets) {
    final words = updated.putIfAbsent(target.cueIndex, () => <int>{});
    if (selected) {
      words.add(target.wordIndex);
    } else {
      words.remove(target.wordIndex);
    }
    if (words.isEmpty) updated.remove(target.cueIndex);
  }
  return updated;
}

List<_AlignedWord> _flattenWords(List<SrtCue> cues, Iterable<int> cueIndexes) {
  final words = <_AlignedWord>[];
  for (final cueIndex in cueIndexes) {
    if (cueIndex < 0 || cueIndex >= cues.length) continue;
    for (final part in tokenizeLessonText(cues[cueIndex].text)) {
      if (!part.isWord || part.wordIndex == null) continue;
      words.add(
        _AlignedWord(
          location: _WordLocation(
            cueIndex: cueIndex,
            wordIndex: part.wordIndex!,
          ),
          normalized: part.text.toLowerCase().replaceAll(RegExp(r"['’–-]"), ''),
        ),
      );
    }
  }
  return words;
}

Map<_WordLocation, _WordLocation> _alignRepeatedWords(
  List<_AlignedWord> primary,
  List<_AlignedWord> repeated,
) {
  final lengths = List.generate(
    primary.length + 1,
    (_) => List<int>.filled(repeated.length + 1, 0),
  );
  for (var left = primary.length - 1; left >= 0; left--) {
    for (var right = repeated.length - 1; right >= 0; right--) {
      lengths[left][right] =
          primary[left].normalized == repeated[right].normalized
          ? lengths[left + 1][right + 1] + 1
          : lengths[left + 1][right] >= lengths[left][right + 1]
          ? lengths[left + 1][right]
          : lengths[left][right + 1];
    }
  }

  final result = <_WordLocation, _WordLocation>{};
  var left = 0;
  var right = 0;
  while (left < primary.length && right < repeated.length) {
    if (primary[left].normalized == repeated[right].normalized) {
      result[primary[left].location] = repeated[right].location;
      result[repeated[right].location] = primary[left].location;
      left += 1;
      right += 1;
    } else if (lengths[left + 1][right] >= lengths[left][right + 1]) {
      left += 1;
    } else {
      right += 1;
    }
  }
  return result;
}

class _AlignedWord {
  const _AlignedWord({required this.location, required this.normalized});

  final _WordLocation location;
  final String normalized;
}

class _WordLocation {
  const _WordLocation({required this.cueIndex, required this.wordIndex});

  final int cueIndex;
  final int wordIndex;

  @override
  bool operator ==(Object other) =>
      other is _WordLocation &&
      other.cueIndex == cueIndex &&
      other.wordIndex == wordIndex;

  @override
  int get hashCode => Object.hash(cueIndex, wordIndex);
}
