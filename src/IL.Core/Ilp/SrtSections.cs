using System.Text.RegularExpressions;
namespace IL.Core.Ilp;
public sealed record SrtTranscriptSection(string Label, IReadOnlyList<int> CueIndexes);
public sealed record SrtTranscriptStructure(IReadOnlyList<SrtTranscriptSection> Sections, IReadOnlySet<int> MarkerCueIndexes, IReadOnlyDictionary<int,int> SectionIndexByCue)
{
    public bool HasMarkers => MarkerCueIndexes.Count > 0;
    public int? SectionIndexForCue(int index) => SectionIndexByCue.TryGetValue(index, out var section) ? section : null;
    public static SrtTranscriptStructure FromCues(IReadOnlyList<SrtCue> cues, bool automatic = true)
    {
        var sections = new List<SrtTranscriptSection>(); var markers = new HashSet<int>(); var byCue = new Dictionary<int,int>();
        var label = "原文"; var indexes = new List<int>();
        void Flush() { if (indexes.Count == 0) return; sections.Add(new(label,indexes.ToArray())); indexes = []; }
        for (var i = 0; i < cues.Count; i++)
        {
            var marker = automatic ? MarkerLabel(cues[i].Text) : null;
            if (marker == null) indexes.Add(i); else { Flush(); markers.Add(i); label = marker; }
        }
        Flush();
        if (markers.Count > 0 && sections.Count > 0 && sections[0].Label == "原文") sections[0] = sections[0] with {Label = "题前原文"};
        for (var i=0;i<sections.Count;i++) foreach (var index in sections[i].CueIndexes) byCue[index]=i;
        return new(sections,markers,byCue);
    }
    public static string? MarkerLabel(string text)
    {
        var compact = Regex.Replace(text.Trim(), @"\s+", " ");
        var match = Regex.Match(compact,@"^Text\s+([A-Za-z0-9_-]+)\s*[.。:：-]?\s*$",RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(compact,@"^[Tt]ext(\d+|[A-Z]{1,4})\s*[.。:：-]?\s*$");
        return match.Success ? "Text " + match.Groups[1].Value : null;
    }
}
