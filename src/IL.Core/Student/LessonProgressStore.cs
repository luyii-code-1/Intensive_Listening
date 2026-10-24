using System.Text.Json;
using IL.Core.Infrastructure;

namespace IL.Core.Student;

public sealed record LessonProgress(string PackageUuid, long PositionMs, DateTimeOffset? LastOpenedAt, IReadOnlySet<string> RevealedCloze)
{
    public IReadOnlyDictionary<string, int> SelectedAnswers { get; init; } = new Dictionary<string, int>();
    public IReadOnlySet<string> RevealedAnswers { get; init; } = new HashSet<string>();
}

public sealed class LessonProgressStore(string? path = null)
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private string FilePath => path ?? Path.Combine(AppDirectories.DataDirectory(), "lesson_progress.json");
    public async Task<Dictionary<string, LessonProgress>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, ct));
            var root = json.RootElement;
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array) return [];
            var result = new Dictionary<string, LessonProgress>();
            foreach (var record in records.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("packageUuid", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) continue;
                var ms = record.TryGetProperty("positionMs", out var pos) && pos.ValueKind == JsonValueKind.Number && pos.TryGetDouble(out var position)
                    ? (long)Math.Clamp(Math.Round(position, MidpointRounding.AwayFromZero), 0, 1L << 31) : 0;
                DateTimeOffset? opened = record.TryGetProperty("lastOpenedAt", out var date) && date.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : null;
                var revealed = record.TryGetProperty("revealedCloze", out var blanks) && blanks.ValueKind == JsonValueKind.Array
                    ? blanks.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet() : [];
                var selected = new Dictionary<string, int>();
                if (record.TryGetProperty("selectedAnswers", out var answers) && answers.ValueKind == JsonValueKind.Object)
                    foreach (var answer in answers.EnumerateObject())
                        if (answer.Name.Length > 0 && answer.Value.ValueKind == JsonValueKind.Number && answer.Value.TryGetInt32(out var index) && index >= 0) selected[answer.Name] = index;
                var revealedAnswers = record.TryGetProperty("revealedAnswers", out var shown) && shown.ValueKind == JsonValueKind.Array
                    ? shown.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet() : [];
                result[id.GetString()!] = new(id.GetString()!, ms, opened, revealed) { SelectedAnswers = selected, RevealedAnswers = revealedAnswers };
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return []; }
    }
    public async Task SaveAsync(IReadOnlyDictionary<string, LessonProgress> records, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { version = 1, records = records.Values.Select(p => new
        {
            packageUuid = p.PackageUuid, positionMs = p.PositionMs, lastOpenedAt = p.LastOpenedAt?.ToString("O"), revealedCloze = p.RevealedCloze.Order().ToArray(), selectedAnswers = p.SelectedAnswers, revealedAnswers = p.RevealedAnswers.Order().ToArray()
        }) }, new JsonSerializerOptions { WriteIndented = true });
        await _writes.WaitAsync(ct);
        var file = FilePath;
        var temporary = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, file, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } finally { _writes.Release(); } }
    }
}
