class LessonQuestion {
  const LessonQuestion({
    required this.id,
    required this.title,
    required this.cueIndexes,
    this.repeatedCueIndexes = const [],
    this.materialId = '',
    this.number = 0,
    this.options = const [],
    this.answerIndex,
  });

  final String id;
  final String title;
  final List<int> cueIndexes;
  final List<int> repeatedCueIndexes;
  final String materialId;
  final int number;
  final List<String> options;
  final int? answerIndex;

  LessonQuestion copyWith({
    String? title,
    int? number,
    List<String>? options,
    Object? answerIndex = _unchangedAnswer,
  }) => LessonQuestion(
    id: id,
    title: title ?? this.title,
    cueIndexes: cueIndexes,
    repeatedCueIndexes: repeatedCueIndexes,
    materialId: materialId,
    number: number ?? this.number,
    options: options ?? this.options,
    answerIndex: identical(answerIndex, _unchangedAnswer)
        ? this.answerIndex
        : answerIndex as int?,
  );

  bool containsRepeatedCue(int cueIndex) =>
      repeatedCueIndexes.contains(cueIndex);

  Map<String, dynamic> toJson() => {
    'id': id,
    'title': title,
    'number': number,
    'materialId': materialId,
    'cueIndexes': cueIndexes,
    'repeatedCueIndexes': repeatedCueIndexes,
    'options': options,
    'answerIndex': answerIndex,
  };

  factory LessonQuestion.fromJson(Map<String, dynamic> json) {
    final cueIndexes = _indexesFrom(json['cueIndexes']);
    final cueIndexSet = cueIndexes.toSet();
    return LessonQuestion(
      id: json['id'] is String ? json['id'] as String : '',
      title: json['title'] is String ? json['title'] as String : '',
      cueIndexes: cueIndexes,
      repeatedCueIndexes: _indexesFrom(json['repeatedCueIndexes'])
          .where(cueIndexSet.contains)
          .toList(growable: false),
      materialId: json['materialId'] is String
          ? json['materialId'] as String
          : '',
      number: json['number'] is num ? (json['number'] as num).round() : 0,
      options: (json['options'] as List? ?? const [])
          .whereType<String>()
          .toList(growable: false),
      answerIndex:
          json['answerIndex'] is int &&
              (json['answerIndex'] as int) >= 0 &&
              (json['answerIndex'] as int) <
                  (json['options'] as List? ?? const []).length
          ? json['answerIndex'] as int
          : null,
    );
  }

  LessonQuestion withMaterial(LessonMaterial material, {int? fallbackNumber}) {
    return LessonQuestion(
      id: id,
      title: title,
      cueIndexes: material.cueIndexes,
      repeatedCueIndexes: material.repeatedCueIndexes,
      materialId: material.id,
      number: number > 0 ? number : fallbackNumber ?? 0,
      options: options,
      answerIndex: answerIndex,
    );
  }
}

const _unchangedAnswer = Object();

class LessonMaterial {
  const LessonMaterial({
    required this.id,
    required this.prompt,
    required this.cueIndexes,
    required this.questionIds,
    this.repeatedCueIndexes = const [],
    this.leadInCueIndexes = const [],
  });

  final String id;
  final String prompt;
  final List<int> cueIndexes;
  final List<int> repeatedCueIndexes;
  final List<int> leadInCueIndexes;
  final List<String> questionIds;

  bool containsCue(int cueIndex) =>
      cueIndexes.contains(cueIndex) || leadInCueIndexes.contains(cueIndex);
  bool containsRepeatedCue(int cueIndex) =>
      repeatedCueIndexes.contains(cueIndex);

  Map<String, dynamic> toJson() => {
    'id': id,
    'prompt': prompt,
    'cueIndexes': cueIndexes,
    'repeatedCueIndexes': repeatedCueIndexes,
    'leadInCueIndexes': leadInCueIndexes,
    'questionIds': questionIds,
  };

  factory LessonMaterial.fromJson(Map<String, dynamic> json) {
    final cueIndexes = _indexesFrom(json['cueIndexes']);
    final cueIndexSet = cueIndexes.toSet();
    return LessonMaterial(
      id: json['id'] is String ? json['id'] as String : '',
      prompt: json['prompt'] is String ? json['prompt'] as String : '',
      cueIndexes: cueIndexes,
      repeatedCueIndexes: _indexesFrom(json['repeatedCueIndexes'])
          .where(cueIndexSet.contains)
          .toList(growable: false),
      leadInCueIndexes: _indexesFrom(json['leadInCueIndexes'])
          .where((index) => !cueIndexSet.contains(index))
          .toList(growable: false),
      questionIds: (json['questionIds'] as List? ?? const [])
          .whereType<String>()
          .where((id) => id.isNotEmpty)
          .toSet()
          .toList(growable: false),
    );
  }
}

class LessonExercises {
  const LessonExercises({
    this.materials = const [],
    this.questions = const [],
    this.clozeWordIndexes = const {},
  });

  final List<LessonMaterial> materials;
  final List<LessonQuestion> questions;
  final Map<int, Set<int>> clozeWordIndexes;

  bool get isEmpty => questions.isEmpty && clozeWordIndexes.isEmpty;

  List<LessonMaterial> get effectiveMaterials {
    if (materials.isNotEmpty) return materials;
    return [
      for (final question in questions)
        LessonMaterial(
          id: question.materialId.isEmpty
              ? 'legacy-${question.id}'
              : question.materialId,
          prompt: '',
          cueIndexes: question.cueIndexes,
          repeatedCueIndexes: question.repeatedCueIndexes,
          questionIds: [question.id],
        ),
    ];
  }

  List<LessonQuestion> questionsForMaterial(LessonMaterial material) {
    final byId = {for (final question in questions) question.id: question};
    return [
      for (var index = 0; index < material.questionIds.length; index++)
        if (byId[material.questionIds[index]] case final question?)
          question.withMaterial(material, fallbackNumber: index + 1),
    ];
  }

  int? materialIndexForCue(int cueIndex) {
    final values = effectiveMaterials;
    for (var index = 0; index < values.length; index++) {
      if (values[index].containsCue(cueIndex)) return index;
    }
    return null;
  }

  LessonMaterial? materialForCue(int cueIndex) {
    final index = materialIndexForCue(cueIndex);
    return index == null ? null : effectiveMaterials[index];
  }

  LessonMaterial? materialForQuestion(String questionId) => effectiveMaterials
      .where((material) => material.questionIds.contains(questionId))
      .firstOrNull;

  int? questionIndexForCue(int cueIndex) {
    final material = materialForCue(cueIndex);
    if (material == null || material.questionIds.isEmpty) return null;
    final index = questions.indexWhere(
      (question) => question.id == material.questionIds.first,
    );
    return index < 0 ? null : index;
  }

  Map<String, dynamic> toJson() => {
    'materials': [for (final material in effectiveMaterials) material.toJson()],
    'questions': [for (final question in questions) question.toJson()],
    'cloze': {
      for (final entry in clozeWordIndexes.entries)
        '${entry.key}': (entry.value.toList()..sort()),
    },
  };

  factory LessonExercises.fromJson(Object? value) {
    if (value is! Map<String, dynamic>) return const LessonExercises();
    final decodedQuestions = (value['questions'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LessonQuestion.fromJson)
        .where((question) => question.id.isNotEmpty)
        .toList(growable: false);
    final rawMaterials = (value['materials'] as List? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(LessonMaterial.fromJson)
        .where(
          (material) =>
              material.id.isNotEmpty && material.cueIndexes.isNotEmpty,
        )
        .toList(growable: false);

    final questions = <LessonQuestion>[];
    final materials = <LessonMaterial>[];
    final assignedCues = <int>{};
    if (rawMaterials.isNotEmpty) {
      final byId = {
        for (final question in decodedQuestions) question.id: question,
      };
      var fallbackNumber = 1;
      for (final material in rawMaterials) {
        final cueIndexes = material.cueIndexes
            .where(assignedCues.add)
            .toList(growable: false);
        if (cueIndexes.isEmpty) continue;
        final normalizedMaterial = LessonMaterial(
          id: material.id,
          prompt: material.prompt,
          cueIndexes: cueIndexes,
          repeatedCueIndexes: material.repeatedCueIndexes
              .where(cueIndexes.contains)
              .toList(growable: false),
          leadInCueIndexes: material.leadInCueIndexes
              .where(
                (index) =>
                    !cueIndexes.contains(index) && assignedCues.add(index),
              )
              .toList(growable: false),
          questionIds: material.questionIds,
        );
        final acceptedIds = <String>[];
        for (final questionId in material.questionIds) {
          final question = byId[questionId];
          if (question == null) continue;
          acceptedIds.add(questionId);
          questions.add(
            question.withMaterial(
              normalizedMaterial,
              fallbackNumber: fallbackNumber,
            ),
          );
          fallbackNumber += 1;
        }
        if (acceptedIds.isEmpty) continue;
        materials.add(
          LessonMaterial(
            id: normalizedMaterial.id,
            prompt: normalizedMaterial.prompt,
            cueIndexes: normalizedMaterial.cueIndexes,
            repeatedCueIndexes: normalizedMaterial.repeatedCueIndexes,
            leadInCueIndexes: normalizedMaterial.leadInCueIndexes,
            questionIds: acceptedIds,
          ),
        );
      }
    } else {
      for (var index = 0; index < decodedQuestions.length; index++) {
        final question = decodedQuestions[index];
        final cueIndexes = question.cueIndexes
            .where(assignedCues.add)
            .toList(growable: false);
        if (cueIndexes.isEmpty) continue;
        final material = LessonMaterial(
          id: question.materialId.isEmpty
              ? 'legacy-${question.id}'
              : question.materialId,
          prompt: '',
          cueIndexes: cueIndexes,
          repeatedCueIndexes: question.repeatedCueIndexes
              .where(cueIndexes.contains)
              .toList(growable: false),
          questionIds: [question.id],
        );
        materials.add(material);
        questions.add(
          question.withMaterial(material, fallbackNumber: index + 1),
        );
      }
    }

    questions.sort((left, right) => left.number.compareTo(right.number));
    final cloze = <int, Set<int>>{};
    final rawCloze = value['cloze'];
    if (rawCloze is Map<String, dynamic>) {
      for (final entry in rawCloze.entries) {
        final cueIndex = int.tryParse(entry.key);
        if (cueIndex == null || cueIndex < 0 || entry.value is! List) continue;
        final indexes = (entry.value as List)
            .whereType<num>()
            .map((item) => item.round())
            .where((item) => item >= 0)
            .toSet();
        if (indexes.isNotEmpty) cloze[cueIndex] = indexes;
      }
    }
    return LessonExercises(
      materials: List.unmodifiable(materials),
      questions: List.unmodifiable(questions),
      clozeWordIndexes: cloze,
    );
  }
}

List<int> _indexesFrom(Object? value) {
  final indexes = (value as List? ?? const [])
      .whereType<num>()
      .map((item) => item.round())
      .where((item) => item >= 0)
      .toSet()
      .toList();
  indexes.sort();
  return indexes;
}

class LessonTextPart {
  const LessonTextPart({
    required this.text,
    required this.isWord,
    this.wordIndex,
  });

  final String text;
  final bool isWord;
  final int? wordIndex;
}

List<LessonTextPart> tokenizeLessonText(String text) {
  final parts = <LessonTextPart>[];
  final matches = RegExp(r"[A-Za-z]+(?:['’-][A-Za-z]+)*").allMatches(text);
  var cursor = 0;
  var wordIndex = 0;
  for (final match in matches) {
    if (match.start > cursor) {
      parts.add(
        LessonTextPart(
          text: text.substring(cursor, match.start),
          isWord: false,
        ),
      );
    }
    parts.add(
      LessonTextPart(text: match.group(0)!, isWord: true, wordIndex: wordIndex),
    );
    wordIndex += 1;
    cursor = match.end;
  }
  if (cursor < text.length) {
    parts.add(LessonTextPart(text: text.substring(cursor), isWord: false));
  }
  return parts;
}
