using System.Text.Json;
using System.Text.Json.Serialization;

namespace IL.Core.Models;

public sealed record LessonQuestion(string Id, string Title, IReadOnlyList<int> CueIndexes,
    IReadOnlyList<int> RepeatedCueIndexes, string MaterialId, int Number, IReadOnlyList<string> Options, int? AnswerIndex);
public sealed record LessonMaterial(string Id, string Prompt, IReadOnlyList<int> CueIndexes,
    IReadOnlyList<int> RepeatedCueIndexes, IReadOnlyList<int> LeadInCueIndexes, IReadOnlyList<string> QuestionIds);

public sealed record LessonExercises(IReadOnlyList<LessonMaterial> Materials, IReadOnlyList<LessonQuestion> Questions,
    [property: JsonPropertyName("cloze")] IReadOnlyDictionary<int, int[]> ClozeWordIndexes)
{
    public static LessonExercises Empty { get; } = new([], [], new Dictionary<int, int[]>());

    [JsonIgnore]
    public IReadOnlyList<LessonMaterial> EffectiveMaterials => Materials.Count > 0 ? Materials
        : Questions.Select(q => new LessonMaterial(q.MaterialId.Length > 0 ? q.MaterialId : $"legacy-{q.Id}", "",
            q.CueIndexes, q.RepeatedCueIndexes, [], [q.Id])).ToArray();
    public LessonMaterial? MaterialForCue(int index) => EffectiveMaterials.FirstOrDefault(m => m.CueIndexes.Contains(index) || m.LeadInCueIndexes.Contains(index));
    public LessonMaterial? MaterialForQuestion(string id) => EffectiveMaterials.FirstOrDefault(m => m.QuestionIds.Contains(id));
    public IReadOnlyList<LessonQuestion> QuestionsForMaterial(LessonMaterial material) => material.QuestionIds
        .Select(id => Questions.FirstOrDefault(q => q.Id == id)).OfType<LessonQuestion>().ToArray();

    public static LessonExercises FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return Empty;
        var questions = Items(json, "questions").Select(q =>
        {
            var cues = Indexes(q, "cueIndexes");
            var options = Strings(q, "options");
            int? answer = q.TryGetProperty("answerIndex", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var i) && i >= 0 && i < options.Length ? i : null;
            return new LessonQuestion(Text(q, "id"), Text(q, "title"), cues,
                Indexes(q, "repeatedCueIndexes").Where(cues.Contains).ToArray(), Text(q, "materialId"), Number(q, "number"), options, answer);
        }).Where(q => q.Id.Length > 0).ToArray();
        var rawMaterials = Items(json, "materials").Select(m =>
        {
            var cues = Indexes(m, "cueIndexes");
            return new LessonMaterial(Text(m, "id"), Text(m, "prompt"), cues,
                Indexes(m, "repeatedCueIndexes").Where(cues.Contains).ToArray(),
                Indexes(m, "leadInCueIndexes").Where(i => !cues.Contains(i)).ToArray(), Strings(m, "questionIds").Distinct().ToArray());
        }).Where(m => m.Id.Length > 0 && m.CueIndexes.Count > 0).ToArray();
        if (rawMaterials.Length == 0)
            rawMaterials = questions.Select(q => new LessonMaterial(q.MaterialId.Length > 0 ? q.MaterialId : $"legacy-{q.Id}", "",
                q.CueIndexes, q.RepeatedCueIndexes, [], [q.Id])).ToArray();
        var byId = questions.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.Last());
        var acceptedQuestions = new List<LessonQuestion>();
        var acceptedMaterials = new List<LessonMaterial>();
        var assigned = new HashSet<int>();
        var nextNumber = 1;
        foreach (var material in rawMaterials)
        {
            var cues = material.CueIndexes.Where(assigned.Add).ToArray();
            if (cues.Length == 0) continue;
            var ids = material.QuestionIds.Where(byId.ContainsKey).ToArray();
            var normalized = material with { CueIndexes = cues, QuestionIds = ids,
                RepeatedCueIndexes = material.RepeatedCueIndexes.Where(cues.Contains).ToArray(),
                LeadInCueIndexes = material.LeadInCueIndexes.Where(i => !cues.Contains(i) && assigned.Add(i)).ToArray() };
            foreach (var id in ids)
            {
                var q = byId[id];
                acceptedQuestions.Add(q with { MaterialId = normalized.Id, CueIndexes = cues,
                    RepeatedCueIndexes = normalized.RepeatedCueIndexes, Number = q.Number > 0 ? q.Number : nextNumber });
                nextNumber++;
            }
            if (ids.Length > 0) acceptedMaterials.Add(normalized);
        }
        var cloze = new Dictionary<int, int[]>();
        if (json.TryGetProperty("cloze", out var blanks) && blanks.ValueKind == JsonValueKind.Object)
            foreach (var entry in blanks.EnumerateObject())
                if (int.TryParse(entry.Name, out var cue) && cue >= 0)
                {
                    var indexes = ReadIndexes(entry.Value);
                    if (indexes.Length > 0) cloze[cue] = indexes;
                }
        return new(acceptedMaterials, acceptedQuestions.OrderBy(q => q.Number).ToArray(), cloze);
    }

    private static IEnumerable<JsonElement> Items(JsonElement obj, string key) => obj.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object) : [];
    private static string Text(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static int Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
        && n >= int.MinValue && n <= int.MaxValue ? (int)Math.Round(n, MidpointRounding.AwayFromZero) : 0;
    private static string[] Strings(JsonElement obj, string key) => obj.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
    private static int[] Indexes(JsonElement obj, string key) => obj.TryGetProperty(key, out var a) ? ReadIndexes(a) : [];
    private static int[] ReadIndexes(JsonElement a) => a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var n) && n >= 0 && n <= int.MaxValue)
            .Select(x => (int)Math.Round(x.GetDouble(), MidpointRounding.AwayFromZero)).Distinct().Order().ToArray() : [];
}
