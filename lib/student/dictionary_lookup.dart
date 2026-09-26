/// The playback word action can use this interface when a dictionary source
/// is configured later. A query always carries its sentence context.
class DictionaryQuery {
  const DictionaryQuery({
    required this.word,
    required this.sentence,
    required this.cueIndex,
  });

  final String word;
  final String sentence;
  final int cueIndex;
}

class DictionaryEntry {
  const DictionaryEntry({required this.headword, required this.definitions});

  final String headword;
  final List<String> definitions;
}

abstract interface class DictionaryLookup {
  Future<DictionaryEntry?> lookup(DictionaryQuery query);
}
