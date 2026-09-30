using System.Security.Cryptography;
using IL.Core.Asr;
namespace IL.Core.Transcription;
public sealed class TranscriptionQueue : IAsyncDisposable
{
    private readonly AsrRunner runner;private readonly Func<string,Task<DuplicateMatch?>> resolveDuplicate;
    private readonly QueueStore store;private readonly SrtRecognitionCache cache;private readonly Func<string> resolveCacheProfile;
    private readonly int maxConcurrent;private readonly int maxRetained;private readonly object sync=new();
    private List<TranscriptionJob> jobs=[];private readonly Dictionary<string,CancellationTokenSource> running=[];
    private readonly HashSet<Task> executions=[];private Task persistence=Task.CompletedTask;private bool disposed;private int sequence;
    public TranscriptionQueue(AsrRunner runner,Func<string,Task<DuplicateMatch?>> resolveDuplicate,QueueStore store,SrtRecognitionCache cache,Func<string> resolveCacheProfile,int maxConcurrent=1,int maxRetained=50)
    {this.runner=runner;this.resolveDuplicate=resolveDuplicate;this.store=store;this.cache=cache;this.resolveCacheProfile=resolveCacheProfile;this.maxConcurrent=Math.Max(1,maxConcurrent);this.maxRetained=Math.Max(0,maxRetained);}
    public event EventHandler? Changed;
    public event Action<TranscriptionJob,bool>? Completed;
    public IReadOnlyList<TranscriptionJob> Jobs {get{lock(sync)return jobs.ToArray();}}
    public bool IsBusy=>Jobs.Any(j=>j.IsActive);
    public int InterruptedCount=>Jobs.Count(j=>j.Status==TranscriptionJobStatus.Interrupted);
    public IReadOnlyList<TranscriptionJob> PendingDecisions=>Jobs.Where(j=>j.Status==TranscriptionJobStatus.AwaitingDecision).ToArray();
    public TranscriptionJob? LatestUnconsumedSrt=>Jobs.FirstOrDefault(j=>j.Status==TranscriptionJobStatus.Completed&&!j.SrtConsumed&&!string.IsNullOrEmpty(j.Srt));
    public TranscriptionJob? JobById(string id){lock(sync)return jobs.FirstOrDefault(j=>j.Id==id);}
    public string Enqueue(string title,string audioPath,TimeSpan? audioDuration=null,string? projectId=null)
    {
        string id;lock(sync)
        {
            ObjectDisposedException.ThrowIf(disposed,this);id=$"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{++sequence}";
            jobs.Insert(0,new(id,title,audioPath,TranscriptionJobStatus.Queued,TranscriptionStage.Queued,"等待前面的任务完成。",DateTimeOffset.Now,projectId,CacheProfile:resolveCacheProfile(),AudioDuration:audioDuration));PersistLocked();
        }
        Notify();Pump();return id;
    }
    public void Resolve(string id,bool proceed)
    {
        var job=JobById(id);if(job?.Status!=TranscriptionJobStatus.AwaitingDecision)return;
        Update(id,j=>proceed?j with{Status=TranscriptionJobStatus.Queued,Stage=TranscriptionStage.Queued,Duplicate=null,DuplicateApproved=true,Fraction=null,Message="等待前面的任务完成。"}:j with{Status=TranscriptionJobStatus.Canceled,Duplicate=null,Fraction=null,FinishedAt=DateTimeOffset.Now,Message="任务已取消。"},true);Trim();Pump();
    }
    public void Cancel(string id)
    {
        var job=JobById(id);if(job==null||job.IsTerminal)return;
        if(job.Status==TranscriptionJobStatus.Running){Update(id,j=>j with{Message="正在取消。"});lock(sync)if(running.TryGetValue(id,out var cancellation))cancellation.Cancel();}
        else{Update(id,j=>j with{Status=TranscriptionJobStatus.Canceled,Duplicate=null,Fraction=null,FinishedAt=DateTimeOffset.Now,Message="任务已取消。"},true);Trim();Pump();}
    }
    public void Retry(string id)
    {
        var job=JobById(id);if(job==null||job.Status is not (TranscriptionJobStatus.Failed or TranscriptionJobStatus.Canceled or TranscriptionJobStatus.Interrupted))return;
        Update(id,j=>j with{Status=TranscriptionJobStatus.Queued,Stage=TranscriptionStage.Queued,Message="等待前面的任务完成。",Fraction=null,StartedAt=null,FinishedAt=null,Duplicate=null,Srt=null,SrtConsumed=false},true);Pump();
    }
    public void MarkSrtConsumed(string id)=>Update(id,j=>j with{SrtConsumed=true,Srt=null},true);
    public void Remove(string id){if(JobById(id)?.IsTerminal==true)Delete(id);}
    public void Delete(string id)
    {
        lock(sync){if(running.TryGetValue(id,out var cancellation))cancellation.Cancel();jobs.RemoveAll(j=>j.Id==id);PersistLocked();}Notify();Pump();
    }
    public void ClearFinished(){lock(sync){jobs.RemoveAll(j=>j.IsTerminal);PersistLocked();}Notify();}
    public async Task RestoreAsync(CancellationToken ct=default)
    {
        var stored=await store.LoadAsync(ct);lock(sync)
        {
            if(disposed)return;var existing=jobs.Select(j=>j.Id).ToHashSet();
            jobs.AddRange(stored.Where(j=>!existing.Contains(j.Id)).Select(j=>j.IsTerminal?j:j with{Status=TranscriptionJobStatus.Interrupted,Fraction=null,Duplicate=null}));
            jobs=jobs.OrderByDescending(j=>j.EnqueuedAt).ToList();
        }
        Notify();
    }
    public void ResumeInterrupted()
    {
        lock(sync){jobs=jobs.Select(j=>j.Status==TranscriptionJobStatus.Interrupted?j with{Status=TranscriptionJobStatus.Queued,Stage=TranscriptionStage.Queued,Fraction=null,Message="等待前面的任务完成。"}:j).ToList();PersistLocked();}Notify();Pump();
    }
    public Task FlushAsync(){lock(sync)return persistence;}
    public async Task WaitForIdleAsync(CancellationToken ct=default)
    {
        while(true){Task[] tasks;lock(sync){tasks=executions.ToArray();if(tasks.Length==0&&!jobs.Any(j=>j.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running))break;}if(tasks.Length>0)await Task.WhenAll(tasks).WaitAsync(ct);else await Task.Delay(10,ct);}
        await FlushAsync().WaitAsync(ct);
    }
    private void Pump()
    {
        var starts=new List<(string Id,CancellationTokenSource Cancellation)>();lock(sync)
        {
            if(disposed)return;
            while(running.Count<maxConcurrent)
            {
                var index=jobs.FindLastIndex(j=>j.Status==TranscriptionJobStatus.Queued);if(index<0)break;
                var job=jobs[index];var cancellation=new CancellationTokenSource();running[job.Id]=cancellation;
                jobs[index]=job with{Status=TranscriptionJobStatus.Running,Stage=TranscriptionStage.Fingerprinting,StartedAt=DateTimeOffset.Now,Fraction=null,BytesDone=0,BytesTotal=0,Message="正在计算音频指纹。"};
                starts.Add((job.Id,cancellation));
            }
            if(starts.Count>0)PersistLocked();
            foreach(var (id,cancellation) in starts)
            {
                var task=Task.Run(()=>ExecuteAsync(id,cancellation));executions.Add(task);
                _=task.ContinueWith(completed=>{lock(sync)executions.Remove(completed);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        if(starts.Count>0)Notify();
    }
    private async Task ExecuteAsync(string id,CancellationTokenSource cancellation)
    {
        var ct=cancellation.Token;
        try
        {
            var job=JobById(id);if(job==null)return;
            if(job.Sha256==null||job.AudioMd5==null)
            {
                var hash=await HashAsync(job.AudioPath,(done,total)=>Update(id,j=>j with{Fraction=total==0?0.05:done/(double)total*0.05,BytesDone=done,BytesTotal=total}),ct);
                Update(id,j=>j with{Sha256=hash.Sha256,AudioMd5=hash.Md5,CacheProfile=j.CacheProfile??resolveCacheProfile()});job=JobById(id);if(job==null)return;
            }
            ct.ThrowIfCancellationRequested();var profile=job.CacheProfile??resolveCacheProfile();var srt=await cache.ReadAsync(job.AudioMd5!,profile,ct);
            if(srt!=null){Complete(id,srt,true);return;}
            DuplicateMatch? duplicate=null;
            if(!job.DuplicateApproved)
            {
                var other=Jobs.FirstOrDefault(j=>j.Id!=id&&j.Sha256==job.Sha256&&j.Status is not (TranscriptionJobStatus.Failed or TranscriptionJobStatus.Canceled or TranscriptionJobStatus.Interrupted)&&(j.Status!=TranscriptionJobStatus.Completed||!string.IsNullOrWhiteSpace(j.Srt)));
                duplicate=other==null?await resolveDuplicate(job.Sha256!):new(DuplicateCase.SharedAudioJob,string.IsNullOrEmpty(other.Title)?"未命名任务":other.Title,JobId:other.Id);
            }
            ct.ThrowIfCancellationRequested();
            if(duplicate!=null){Update(id,j=>j with{Status=TranscriptionJobStatus.AwaitingDecision,Stage=TranscriptionStage.Queued,Fraction=null,BytesDone=0,BytesTotal=0,Duplicate=duplicate,Message="等待确认是否继续。"},true);return;}
            Update(id,j=>j with{Stage=TranscriptionStage.Uploading,Fraction=0.05,Message="正在发送音频。"});
            srt=await runner(job.AudioPath,new InlineProgress(p=>ApplyProgress(id,p)),ct,EstimateProcessingTime(job.AudioDuration));ct.ThrowIfCancellationRequested();
            await cache.WriteAsync(job.AudioMd5!,profile,srt,ct);ct.ThrowIfCancellationRequested();Complete(id,srt,false);
        }
        catch(OperationCanceledException){if(!disposed)Update(id,j=>j with{Status=TranscriptionJobStatus.Canceled,Fraction=null,FinishedAt=DateTimeOffset.Now,Message="任务已取消。"},true);}
        catch(Exception ex){if(!disposed)Update(id,j=>j with{Status=TranscriptionJobStatus.Failed,Fraction=null,FinishedAt=DateTimeOffset.Now,Message=ex is AsrException?ex.Message:"转写失败："+ex.Message},true);}
        finally{lock(sync)running.Remove(id);cancellation.Dispose();Trim();Pump();}
    }
    private sealed class InlineProgress(Action<AsrProgress> callback):IProgress<AsrProgress>{public void Report(AsrProgress value)=>callback(value);}
    private void Complete(string id,string srt,bool cacheHit)
    {
        Update(id,j=>j with{Status=TranscriptionJobStatus.Completed,Stage=TranscriptionStage.Formatting,Fraction=1,Srt=srt,SrtConsumed=false,FinishedAt=DateTimeOffset.Now,Message=cacheHit?(string.IsNullOrWhiteSpace(srt)?"已从缓存恢复，音频中没有可用语音。":"已从缓存恢复 SRT。"):(string.IsNullOrWhiteSpace(srt)?"识别完成，音频中没有可用语音。":"字幕已写入 SRT。")},true);
        if(JobById(id) is {} completed)Completed?.Invoke(completed,cacheHit);
    }
    private void ApplyProgress(string id,AsrProgress p)
    {
        var (stage,fraction,message)=p.Stage switch
        {
            AsrStage.Decoding=>(TranscriptionStage.Decoding,0.07,"正在分析音频。"),AsrStage.Slicing=>(TranscriptionStage.Slicing,0.1,"正在按静音位置切分音频。"),
            AsrStage.Uploading=>(TranscriptionStage.Uploading,0.1+(p.StageFraction??0)*0.05,$"正在发送音频，{p.BytesDone/(1024d*1024):F1} / {p.BytesTotal/(1024d*1024):F1} MB。"),
            AsrStage.Recognizing=>(TranscriptionStage.Recognizing,0.15+(p.StageFraction??0)*0.8,p.SegmentTotal==null?"服务器正在识别。":"正在并行识别音频分段。"),
            AsrStage.Formatting=>(TranscriptionStage.Formatting,0.97,"正在写入 SRT。"),_=>(TranscriptionStage.Merging,0.99,"正在合并字幕时间轴。")
        };
        Update(id,j=>j with{Stage=stage,Fraction=fraction,Message=message,BytesDone=p.BytesDone,BytesTotal=p.BytesTotal,SegmentIndex=p.SegmentIndex,SegmentTotal=p.SegmentTotal});
    }
    private void Update(string id,Func<TranscriptionJob,TranscriptionJob> transform,bool persist=false)
    {lock(sync){var index=jobs.FindIndex(j=>j.Id==id);if(index<0||disposed)return;jobs[index]=transform(jobs[index]);if(persist)PersistLocked();}Notify();}
    private void Trim()
    {lock(sync){var keep=jobs.Where(j=>j.IsTerminal).Take(maxRetained).Select(j=>j.Id).ToHashSet();jobs.RemoveAll(j=>j.IsTerminal&&!keep.Contains(j.Id));if(!disposed)PersistLocked();}}
    private void PersistLocked(){var snapshot=jobs.ToArray();persistence=persistence.ContinueWith(async _=>await store.SaveAsync(snapshot),CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default).Unwrap();}
    private void Notify(){if(!disposed)Changed?.Invoke(this,EventArgs.Empty);}
    public static TimeSpan? EstimateProcessingTime(TimeSpan? duration)=>duration>TimeSpan.Zero?TimeSpan.FromMilliseconds(Math.Clamp(duration.Value.TotalMilliseconds*0.35,20000,3600000)):null;
    private static async Task<(string Md5,string Sha256)> HashAsync(string path,Action<long,long> progress,CancellationToken ct)
    {
        await using var file=File.OpenRead(path);using var md5=IncrementalHash.CreateHash(HashAlgorithmName.MD5);using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[128*1024];long done=0;int read;
        while((read=await file.ReadAsync(buffer,ct))>0){md5.AppendData(buffer.AsSpan(0,read));sha.AppendData(buffer.AsSpan(0,read));done+=read;progress(done,file.Length);}
        return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;lock(sync)
        {
            if(disposed)return;disposed=true;
            jobs=jobs.Select(j=>j.IsActive?j with{Status=TranscriptionJobStatus.Interrupted,Fraction=null,Duplicate=null}:j).ToList();
            foreach(var cancellation in running.Values)cancellation.Cancel();PersistLocked();tasks=executions.ToArray();
        }
        await Task.WhenAll(tasks);await FlushAsync();
    }
}
