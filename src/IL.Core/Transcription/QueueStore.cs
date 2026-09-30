using System.Text.Json;
using IL.Core.Infrastructure;
namespace IL.Core.Transcription;
public sealed class QueueStore(string? path=null)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public string FilePath=>path??Path.Combine(AppDirectories.DataDirectory(),"transcription_queue.json");
    public async Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken ct=default)
    {
        if(!File.Exists(FilePath))return [];
        try
        {
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(FilePath,ct));var root=json.RootElement;
            if(root.GetProperty("version").GetInt32()!=1||root.GetProperty("jobs").ValueKind!=JsonValueKind.Array)return [];
            var jobs=new List<TranscriptionJob>();foreach(var e in root.GetProperty("jobs").EnumerateArray())
            {
                if(e.ValueKind!=JsonValueKind.Object)continue;
                string? Text(string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
                DateTimeOffset? Date(string key)=>DateTimeOffset.TryParse(Text(key),out var date)?date:null;
                bool Flag(string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
                var id=Text("id");var audio=Text("audioPath");if(string.IsNullOrEmpty(id)||string.IsNullOrEmpty(audio))continue;
                var status=Enum.TryParse<TranscriptionJobStatus>(Text("status"),true,out var s)?s:TranscriptionJobStatus.Queued;
                var stage=Enum.TryParse<TranscriptionStage>(Text("stage"),true,out var st)?st:TranscriptionStage.Queued;
                var duration=e.TryGetProperty("audioDurationMs",out var durationValue)&&durationValue.ValueKind==JsonValueKind.Number?TimeSpan.FromMilliseconds(durationValue.GetDouble()):(TimeSpan?)null;
                jobs.Add(new(id,Text("title")??"",audio,status,stage,Text("message")??"",Date("enqueuedAt")??DateTimeOffset.Now,Text("projectId"),Sha256:Text("sha256"),AudioMd5:Text("audioMd5"),CacheProfile:Text("cacheProfile"),DuplicateApproved:Flag("duplicateApproved"),AudioDuration:duration,StartedAt:Date("startedAt"),FinishedAt:Date("finishedAt"),Srt:Text("srt"),SrtConsumed:Flag("srtConsumed")));
            }
            return jobs;
        }
        catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return [];}
    }
    public async Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);try
        {
            var objects=jobs.Select(j=>new{id=j.Id,title=j.Title,audioPath=j.AudioPath,status=char.ToLowerInvariant(j.Status.ToString()[0])+j.Status.ToString()[1..],stage=j.Stage.ToString().ToLowerInvariant(),message=j.Message,enqueuedAt=j.EnqueuedAt,projectId=j.ProjectId,startedAt=j.StartedAt,finishedAt=j.FinishedAt,sha256=j.Sha256,audioMd5=j.AudioMd5,cacheProfile=j.CacheProfile,audioDurationMs=j.AudioDuration?.TotalMilliseconds,srt=j.Srt,srtConsumed=j.SrtConsumed,duplicateApproved=j.DuplicateApproved}).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);var temp=FilePath+".tmp";
            await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(new{version=1,jobs=objects},new JsonSerializerOptions{WriteIndented=true}),ct);File.Move(temp,FilePath,true);
        }
        finally{gate.Release();}
    }
}
