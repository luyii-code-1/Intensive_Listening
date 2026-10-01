using System.Text;
using System.Text.RegularExpressions;

namespace IL.Core.Ilp;

public static partial class SrtParser
{
    [GeneratedRegex(@"^(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})(?:\s+.*)?$")]
    private static partial Regex TimePattern();
    [GeneratedRegex(@"^\d+$")] private static partial Regex IndexPattern();

    public static IReadOnlyList<SrtCue> Parse(byte[] bytes, TimeSpan mediaDuration)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n'); }
        catch (DecoderFallbackException ex) { throw new IlpException(IlpError.InvalidTranscript, "SRT 必须使用 UTF-8 编码", ex); }
        if (string.IsNullOrWhiteSpace(text)) throw Invalid("SRT 不能为空");
        var cues = new List<SrtCue>();
        foreach (var block in Regex.Split(text.Trim(), @"\n[ \t]*\n+"))
        {
            var lines = block.Split('\n');
            var timeLine = IndexPattern().IsMatch(lines[0].Trim()) ? 1 : 0;
            if (lines.Length <= timeLine + 1) throw Invalid("SRT 片段缺少时间或文本");
            var match = TimePattern().Match(lines[timeLine].Trim());
            if (!match.Success) throw Invalid("SRT 时间格式无效");
            var start = ReadTime(match, 1);
            var end = ReadTime(match, 5);
            var content = lines[(timeLine + 1)..];
            if (content.Any(line => TimePattern().IsMatch(line.Trim()))) throw Invalid("SRT 片段之间需要空行分隔");
            var cueText = string.Join('\n', content).Trim();
            if (cueText.Length == 0 || end <= start || end > mediaDuration) throw Invalid("SRT 包含空文本或无效时间范围");
            if (cues.Count > 0 && start < cues[^1].End) throw Invalid("SRT 片段时间存在重叠");
            cues.Add(new(start, end, cueText));
        }
        return cues;
    }

    public static string Serialize(IEnumerable<SrtCue> cues)
    {
        var text = new StringBuilder();
        var number = 1;
        foreach (var cue in cues)
            text.Append(number++).Append('\n').Append(Timestamp(cue.Start)).Append(" --> ").Append(Timestamp(cue.End))
                .Append('\n').Append(cue.Text).Append("\n\n");
        return text.ToString();
    }
    public static string Timestamp(TimeSpan time) => $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    private static TimeSpan ReadTime(Match match, int offset)
    {
        try
        {
            var hours = long.Parse(match.Groups[offset].Value);
            var minutes = int.Parse(match.Groups[offset + 1].Value);
            var seconds = int.Parse(match.Groups[offset + 2].Value);
            var milliseconds = int.Parse(match.Groups[offset + 3].Value);
            if (minutes >= 60 || seconds >= 60) throw Invalid("SRT 时间值超出范围");
            return TimeSpan.FromTicks(checked(hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond + milliseconds * TimeSpan.TicksPerMillisecond));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new IlpException(IlpError.InvalidTranscript, "SRT 时间值超出范围", ex); }
    }
    private static IlpException Invalid(string message) => new(IlpError.InvalidTranscript, message);
}
