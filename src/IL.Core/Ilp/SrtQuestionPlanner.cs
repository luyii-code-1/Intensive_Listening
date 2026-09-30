using System.Text.RegularExpressions;
using IL.Core.Models;
namespace IL.Core.Ilp;
public sealed class SrtQuestionPlanner
{
    private static readonly Regex LeadingMarker=new(@"^\s*(?:[Tt]ext\s+[A-Za-z0-9_-]+|[Tt]ext(?:\d+|[A-Z]{1,4}))\s*[:：.。,-]?\s*");
    private static readonly Regex Instruction=new(@"\b(?:listen|you\s+will\s+hear|answer|choose|question|section)\b",RegexOptions.IgnoreCase);
    private static readonly Regex Prompt=new(@"(?:回答|完成|选择).{0,36}(?:小?题|个问题)|(?:听|listen|hear).{0,48}(?:回答|answer|question)",RegexOptions.IgnoreCase);
    private static readonly Regex Words=new(@"[A-Za-z]+(?:['’-][A-Za-z]+)*");
    private static readonly Regex NumberOnly=new(@"^[\d\sA-Ca-c.,，。:：-]+$");
    private static readonly Regex NumberToken=new(@"\d{1,3}|[零〇一二两三四五六七八九十百]{1,5}");
    private sealed record Run(int[] Cues,bool FollowsPrompt,string Prompt,int[] PromptCues,int[] Numbers);
    public static LessonExercises Plan(IReadOnlyList<SrtCue> cues,IReadOnlyDictionary<int,int[]>? clozeWordIndexes=null)
    {
        var materials=new List<LessonMaterial>();var questions=new List<LessonQuestion>();var nextNumber=1;
        foreach(var run in ContentRuns(cues))
        {
            var repetition=RepetitionStart(cues,run.Cues);
            if(repetition==null && !run.FollowsPrompt)continue;
            var repeated=repetition.HasValue ? run.Cues[repetition.Value..] : [];
            var materialId=$"material-{run.Cues[0]}-{run.Cues[^1]}";
            var numbers=run.Numbers.Length==0 ? [nextNumber] : run.Numbers;var ids=new List<string>();
            foreach(var number in numbers)
            {
                var id=$"auto-{number}-{run.Cues[0]}";ids.Add(id);
                questions.Add(new(id,$"第 {number} 题",run.Cues,repeated,materialId,number,[],null));
            }
            nextNumber=numbers.Max()+1;
            materials.Add(new(materialId,run.Prompt,run.Cues,repeated,run.PromptCues.Length==0 ? [] : [run.PromptCues[^1]],ids));
        }
        return new(materials,questions,clozeWordIndexes??new Dictionary<int,int[]>());
    }
    private static IEnumerable<Run> ContentRuns(IReadOnlyList<SrtCue> cues)
    {
        var result=new List<Run>();var indexes=new List<int>();var follows=false;var prompt="";var promptCues=new List<int>();var numbers=Array.Empty<int>();
        void Flush(){if(indexes.Count==0)return;result.Add(new(indexes.ToArray(),follows,prompt,promptCues.ToArray(),numbers));indexes=[];follows=false;prompt="";promptCues=[];numbers=[];}
        for(var i=0;i<cues.Count;i++)
        {
            var cue=cues[i];var text=Regex.Replace(LeadingMarker.Replace(cue.Text,"",1),@"\s+"," ").Trim();
            if(IsNarration(cue,text))
            {
                Flush();var questionPrompt=Prompt.IsMatch(text)||(NumberOnly.IsMatch(text)&&text.Length<=16);
                follows |= questionPrompt;if(follows)promptCues.Add(i);
                if(questionPrompt){prompt=text;numbers=QuestionNumbers(text);}
                continue;
            }
            if(indexes.Count>0 && cue.Start-cues[indexes[^1]].End>TimeSpan.FromSeconds(12))Flush();
            indexes.Add(i);
        }
        Flush();return result;
    }
    private static bool IsNarration(SrtCue cue,string text) => text.Length==0||SrtTranscriptStructure.MarkerLabel(text)!=null||Regex.IsMatch(text,@"[\u3400-\u9fff]")
        ||(Instruction.IsMatch(text)&&Prompt.IsMatch(text))||(NumberOnly.IsMatch(text)&&cue.End-cue.Start>=TimeSpan.FromMilliseconds(1400))
        ||(!Words.IsMatch(text)&&cue.End-cue.Start>=TimeSpan.FromMilliseconds(1400));
    public static int[] QuestionNumbers(string text)
    {
        var normalized=Regex.Replace(text,@"[０-９]",m=>(m.Value[0]-'０').ToString());
        normalized=Regex.Replace(normalized,@"\d(?:\s+\d)+",m=>Regex.Replace(m.Value,@"\s+",""));
        var answers=Regex.Matches(normalized,@"回答|answer",RegexOptions.IgnoreCase);if(answers.Count>0)normalized=normalized[(answers[^1].Index+answers[^1].Length)..];
        var ordinal=normalized.IndexOf('第');if(ordinal>=0)normalized=normalized[(ordinal+1)..];
        var first=NumberToken.Match(normalized);if(!first.Success)return [];
        normalized=normalized[first.Index..];var suffix=Regex.Match(normalized,@"小?题");if(suffix.Success)normalized=normalized[..(suffix.Index+suffix.Length)];
        var numbers=NumberToken.Matches(normalized).Select(m=>ParseNumber(m.Value)).Where(n=>n>0).Distinct().ToArray();
        if(numbers.Length==2 && Regex.IsMatch(normalized,@"(?:至|到|[-–—~～])") && numbers[1]>numbers[0]&&numbers[1]-numbers[0]<=20)
            return Enumerable.Range(numbers[0],numbers[1]-numbers[0]+1).ToArray();
        return numbers;
    }
    private static int ParseNumber(string token)
    {
        if(int.TryParse(token,out var arabic))return arabic;
        int Digit(char c)=>c switch{'零' or '〇'=>0,'一'=>1,'二' or '两'=>2,'三'=>3,'四'=>4,'五'=>5,'六'=>6,'七'=>7,'八'=>8,'九'=>9,_=>-1};
        if(!token.Contains('十')&&!token.Contains('百')){var value=0;foreach(var c in token){var d=Digit(c);if(d<0)return 0;value=value*10+d;}return value;}
        var total=0;var current=0;foreach(var c in token){var d=Digit(c);if(d>=0){current=d;continue;}var unit=c=='百'?100:c=='十'?10:0;if(unit==0)return 0;total+=(current==0?1:current)*unit;current=0;}return total+current;
    }
    private static int? RepetitionStart(IReadOnlyList<SrtCue> cues,int[] indexes)
    {
        var bestScore=0d;int? best=null;
        for(var split=1;split<indexes.Length;split++)
        {
            var left=indexes[..split];var right=indexes[split..];var lt=Tokens(cues,left);var rt=Tokens(cues,right);
            if(lt.Length<3||rt.Length<3)continue;var ratio=(double)lt.Length/rt.Length;if(ratio<0.55||ratio>1.82)continue;
            var ld=cues[left[^1]].End-cues[left[0]].Start;var rd=cues[right[^1]].End-cues[right[0]].Start;
            if(ld<=TimeSpan.Zero||rd<=TimeSpan.Zero)continue;ratio=ld.TotalMilliseconds/rd.TotalMilliseconds;if(ratio<0.55||ratio>1.82)continue;
            var counts=lt.GroupBy(t=>t).ToDictionary(g=>g.Key,g=>g.Count());var intersection=0;
            foreach(var t in rt)if(counts.TryGetValue(t,out var count)&&count>0){intersection++;counts[t]--;}
            var similarity=2d*intersection/(lt.Length+rt.Length);var score=similarity+Math.Clamp((cues[right[0]].Start-cues[left[^1]].End).TotalMilliseconds/5000,0,0.08);
            if(similarity>=0.76&&score>bestScore){bestScore=score;best=split;}
        }
        return best;
    }
    private static string[] Tokens(IReadOnlyList<SrtCue> cues,IEnumerable<int> indexes)=>indexes.SelectMany(i=>Words.Matches(LeadingMarker.Replace(cues[i].Text,"",1)).Select(m=>Regex.Replace(m.Value.ToLowerInvariant(),"['’-]",""))).ToArray();
}
