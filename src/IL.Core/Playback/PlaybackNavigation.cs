using IL.Core.Ilp;
using IL.Core.Models;
namespace IL.Core.Playback;
public static class PlaybackNavigation
{
    public static int ActiveCueIndexForPosition(IReadOnlyList<SrtCue> cues, TimeSpan position)
    { for(var i=cues.Count-1;i>=0;i--) if(position>=cues[i].Start) return i; return cues.Count>0 ? 0 : -1; }
    public static TimeSpan CueSeekPosition(SrtCue cue) => cue.Start+TimeSpan.FromMilliseconds(50)<cue.End ? cue.Start+TimeSpan.FromMilliseconds(50) : cue.Start;
    public static TimeSpan CueNavigationPosition(SrtCue cue) => cue.Start>TimeSpan.FromMilliseconds(200) ? cue.Start-TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
    public static TimeSpan Clamp(TimeSpan value,TimeSpan duration) => value<TimeSpan.Zero ? TimeSpan.Zero : value>duration ? duration : value;
    public static int QuestionIndexForCue(LessonExercises exercises,int cueIndex,string? selectedQuestionId=null)
    {
        var material=exercises.Materials.FirstOrDefault(m=>m.CueIndexes.Contains(cueIndex)||m.LeadInCueIndexes.Contains(cueIndex));
        if(material==null)return -1;
        var candidates=exercises.Questions.Where(q=>q.MaterialId==material.Id).ToArray();
        var question=candidates.FirstOrDefault(q=>q.Id==selectedQuestionId)??candidates.FirstOrDefault();
        return question==null ? -1 : exercises.Questions.ToList().IndexOf(question);
    }
}
