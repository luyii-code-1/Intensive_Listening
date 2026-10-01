namespace IL.Core.Student;
public sealed record DictionaryQuery(string Word, string Sentence, int CueIndex);
public sealed record DictionaryEntry(string Headword, IReadOnlyList<string> Definitions);
public interface IDictionaryLookup { Task<DictionaryEntry?> LookupAsync(DictionaryQuery query, CancellationToken cancellationToken = default); }
