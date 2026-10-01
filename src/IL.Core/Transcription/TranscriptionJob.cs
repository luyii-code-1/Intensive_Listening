using IL.Core.Asr;
using IL.Core.Ilp;
namespace IL.Core.Transcription;
public enum TranscriptionJobStatus { Queued,Running,AwaitingDecision,Completed,Failed,Canceled,Interrupted }
public enum TranscriptionStage { Queued,Fingerprinting,Decoding,Slicing,Uploading,Recognizing,Formatting,Merging }
public enum DuplicateCase { IdenticalPackage,PackageUpdate,SharedAudioLesson,SharedAudioJob }
public sealed record DuplicateMatch(DuplicateCase DuplicateCase,string ExistingTitle,ImportedLesson? Lesson=null,string? LessonId=null,string? JobId=null)
{
    public static DuplicateMatch? FindLessonDuplicate(IEnumerable<ImportedLesson> lessons,IlpManifest manifest)
    {
        var lesson=lessons.FirstOrDefault(l=>l.Id==manifest.PackageUuid);return lesson==null?null:new(lesson.Manifest.PackageVersion==manifest.PackageVersion?DuplicateCase.IdenticalPackage:DuplicateCase.PackageUpdate,lesson.Manifest.Title,lesson,lesson.Id);
    }
}
public delegate Task<string> AsrRunner(string audioPath,IProgress<AsrProgress>? progress,CancellationToken ct,TimeSpan? estimatedProcessingTime);
public sealed record TranscriptionJob(string Id,string Title,string AudioPath,TranscriptionJobStatus Status,TranscriptionStage Stage,string Message,DateTimeOffset EnqueuedAt,
    string? ProjectId=null,double? Fraction=null,bool FractionIsEstimated=false,long BytesDone=0,long BytesTotal=0,int? SegmentIndex=null,int? SegmentTotal=null,
    string? Sha256=null,string? AudioMd5=null,string? CacheProfile=null,DuplicateMatch? Duplicate=null,bool DuplicateApproved=false,TimeSpan? AudioDuration=null,
    DateTimeOffset? StartedAt=null,DateTimeOffset? FinishedAt=null,string? Srt=null,bool SrtConsumed=false)
{
    public bool IsTerminal=>Status is TranscriptionJobStatus.Completed or TranscriptionJobStatus.Failed or TranscriptionJobStatus.Canceled;
    public bool IsActive=>Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running or TranscriptionJobStatus.AwaitingDecision;
}
