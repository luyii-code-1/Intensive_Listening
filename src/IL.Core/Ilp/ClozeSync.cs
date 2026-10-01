using System.Text.RegularExpressions;
using IL.Core.Models;
namespace IL.Core.Ilp;
public static class ClozeSync
{
    private readonly record struct Location(int Cue, int Word);
    public static IReadOnlyDictionary<int,int[]> Toggle(IReadOnlyDictionary<int,int[]> current, IReadOnlyList<SrtCue> cues, LessonExercises exercises, int cueIndex, int wordIndex)
    {
        var updated=current.ToDictionary(x=>x.Key,x=>x.Value.ToHashSet());
        if (cueIndex<0 || cueIndex>=cues.Count || wordIndex<0) return updated.ToDictionary(x=>x.Key,x=>x.Value.Order().ToArray());
        var selected = !updated.TryGetValue(cueIndex,out var selectedWords) || !selectedWords.Contains(wordIndex);
        var target=new Location(cueIndex,wordIndex); var targets=new HashSet<Location>{target};
        var material=exercises.EffectiveMaterials.FirstOrDefault(m=>m.CueIndexes.Contains(cueIndex) || m.LeadInCueIndexes.Contains(cueIndex));
        if (material != null && material.RepeatedCueIndexes.Count>0)
        {
            var primary=Flatten(cues,material.CueIndexes.Except(material.RepeatedCueIndexes)); var repeated=Flatten(cues,material.RepeatedCueIndexes);
            var lengths=new int[primary.Count+1,repeated.Count+1];
            for(var l=primary.Count-1;l>=0;l--) for(var r=repeated.Count-1;r>=0;r--)
                lengths[l,r]=primary[l].Word==repeated[r].Word ? lengths[l+1,r+1]+1 : Math.Max(lengths[l+1,r],lengths[l,r+1]);
            var left=0;var right=0;
            while(left<primary.Count && right<repeated.Count)
            {
                if(primary[left].Word==repeated[right].Word)
                {
                    if(primary[left].Location==target) targets.Add(repeated[right].Location);
                    if(repeated[right].Location==target) targets.Add(primary[left].Location);
                    left++;right++;
                }
                else if(lengths[left+1,right]>=lengths[left,right+1]) left++; else right++;
            }
        }
        foreach(var location in targets)
        {
            if(!updated.TryGetValue(location.Cue,out var words)) updated[location.Cue]=words=[];
            if(selected) words.Add(location.Word);else words.Remove(location.Word);
            if(words.Count==0)updated.Remove(location.Cue);
        }
        return updated.ToDictionary(x=>x.Key,x=>x.Value.Order().ToArray());
    }
    private static List<(Location Location,string Word)> Flatten(IReadOnlyList<SrtCue> cues,IEnumerable<int> indexes)
    {
        var result=new List<(Location,string)>();
        foreach(var cue in indexes.Where(i=>i>=0 && i<cues.Count))
            foreach(var part in LessonTextTokenizer.Tokenize(cues[cue].Text).Where(p=>p.IsWord))
                result.Add((new(cue,part.WordIndex!.Value),Regex.Replace(part.Text.ToLowerInvariant(),"['’–-]","")));
        return result;
    }
}
