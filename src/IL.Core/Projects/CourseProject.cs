using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IL.Core.Models;
namespace IL.Core.Projects;
public enum CourseProjectStep { Audio,Transcription,Review,Completed }
public enum ReviewPhase { Grouping,Cloze }
public sealed record CourseProject(string Id,string Title,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,CourseProjectStep Step,string PackageUuid,
    string? AudioPath=null,TimeSpan? AudioDuration=null,string? TranscriptionJobId=null,string Transcript="",string? LastExportPath=null,int PackageVersion=0,
    ReviewPhase ReviewPhase=ReviewPhase.Grouping,LessonExercises? ExerciseData=null,bool AutomaticQuestionPlanApplied=false,bool AutoQuestionPlanDeferred=false)
{
    public bool HasAudio=>!string.IsNullOrEmpty(AudioPath);
    public bool HasTranscript=>!string.IsNullOrWhiteSpace(Transcript);
    public LessonExercises Exercises {get;init;}=ExerciseData??LessonExercises.Empty;
    internal Dictionary<string,object?> ToMetadata()=>new()
    {
        ["version"]=2,["id"]=Id,["title"]=Title,["createdAt"]=CreatedAt,["updatedAt"]=UpdatedAt,["step"]=Step.ToString().ToLowerInvariant(),
        ["audioPath"]=AudioPath,["audioDurationMs"]=AudioDuration?.TotalMilliseconds,["transcriptionJobId"]=TranscriptionJobId,["lastExportPath"]=LastExportPath,
        ["packageUuid"]=PackageUuid,["packageVersion"]=PackageVersion,["reviewPhase"]=ReviewPhase.ToString().ToLowerInvariant(),["exercises"]=Exercises,
        ["automaticQuestionPlanApplied"]=AutomaticQuestionPlanApplied,["autoQuestionPlanDeferred"]=AutoQuestionPlanDeferred
    };
    internal static CourseProject FromMetadata(JsonElement json,string transcript)
    {
        string Text(string key,string fallback="")=>json.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:fallback;
        string? Nullable(string key)=>Text(key) is {Length:>0} text?text:null;
        double? Number(string key)=>json.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var n)?n:null;
        bool Flag(string key)=>json.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.True;
        var id=Text("id");if(id.Length==0)throw new FormatException("项目 ID 无效");
        var uuid=Nullable("packageUuid")??StableUuid(id);
        return new(id,Text("title"),DateTimeOffset.Parse(Text("createdAt")),DateTimeOffset.Parse(Text("updatedAt")),Enum.TryParse<CourseProjectStep>(Text("step"),true,out var step)?step:CourseProjectStep.Audio,uuid,
            Nullable("audioPath"),Number("audioDurationMs") is {} ms?TimeSpan.FromMilliseconds(ms):null,Nullable("transcriptionJobId"),transcript,Nullable("lastExportPath"),(int)(Number("packageVersion")??0),
            Enum.TryParse<ReviewPhase>(Text("reviewPhase"),true,out var phase)?phase:ReviewPhase.Grouping,json.TryGetProperty("exercises",out var exercises)?LessonExercises.FromJson(exercises):null,Flag("automaticQuestionPlanApplied"),Flag("autoQuestionPlanDeferred"));
    }
    private static string StableUuid(string id)
    {
        var seed=Encoding.UTF8.GetBytes(id);var bytes=Enumerable.Range(0,16).Select(i=>(byte)(seed.Length==0?i:seed[i%seed.Length]^i)).ToArray();
        bytes[6]=(byte)((bytes[6]&0x0f)|0x40);bytes[8]=(byte)((bytes[8]&0x3f)|0x80);var hex=Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
public sealed record CourseProjectExamDocument(CourseProject Project,string SourceName,string DocumentPath,string TextPath,string Text,string Sha256,int ParagraphCount,int TableCount);
public sealed class CourseProjectArchiveException(string message,Exception? inner=null):Exception(message,inner);
public sealed class CourseProjectDocumentException(string message,Exception? inner=null):Exception(message,inner);
