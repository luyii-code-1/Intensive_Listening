using System.Text.RegularExpressions;
namespace IL.Core.Models;
public sealed record LessonTextPart(string Text, bool IsWord, int? WordIndex = null);
public static partial class LessonTextTokenizer
{
    [GeneratedRegex(@"[A-Za-z]+(?:['’-][A-Za-z]+)*")] private static partial Regex Words();
    public static IReadOnlyList<LessonTextPart> Tokenize(string text)
    {
        var parts = new List<LessonTextPart>(); var cursor = 0; var wordIndex = 0;
        foreach (Match match in Words().Matches(text))
        {
            if (match.Index > cursor) parts.Add(new(text[cursor..match.Index], false));
            parts.Add(new(match.Value, true, wordIndex++)); cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) parts.Add(new(text[cursor..], false));
        return parts;
    }
}
