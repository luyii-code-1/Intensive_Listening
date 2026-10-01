using IL.Core.Asr;
using IL.Core.Transcription;
using Xunit;
namespace IL.Core.Tests;
public sealed class TranscriptionWorkflowTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"il2-queue-"+Guid.NewGuid().ToString("N"));
    public TranscriptionWorkflowTests()=>Directory.CreateDirectory(root);
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private QueueStore Store=>new(Path.Combine(root,"queue.json"));
    private SrtRecognitionCache Cache=>new(Path.Combine(root,"cache"));
    private async Task<string> Audio(string name,string? contents=null){var path=Path.Combine(root,name);await File.WriteAllTextAsync(path,contents??name);return path;}
    private TranscriptionQueue Queue(AsrRunner runner,Func<string,Task<DuplicateMatch?>>? duplicates=null,int retained=50)=>new(runner,duplicates??(_=>Task.FromResult<DuplicateMatch?>(null)),Store,Cache,()=>"model/en",maxRetained:retained);
    private static async Task Until(Func<bool> condition)
    {using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,ct.Token);}
    [Fact] public async Task RunsFifoCachesAndConsumesCompletedTranscript()
    {
        var calls=new List<string>();var releases=new Queue<TaskCompletionSource<string>>();
        Task<string> Runner(string audio,IProgress<AsrProgress>? progress,CancellationToken ct,TimeSpan? estimate)
        {lock(calls)calls.Add(Path.GetFileName(audio));var tcs=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);lock(releases)releases.Enqueue(tcs);ct.Register(()=>tcs.TrySetCanceled(ct));return tcs.Task;}
        await using var queue=Queue(Runner);var firstAudio=await Audio("a.wav");var first=queue.Enqueue("A",firstAudio,TimeSpan.FromSeconds(100),"project");var second=queue.Enqueue("B",await Audio("b.wav"));
        await Until(()=>calls.Count==1);Assert.Equal(new[]{"a.wav"},calls);Assert.Equal(TranscriptionJobStatus.Queued,queue.JobById(second)!.Status);
        releases.Dequeue().SetResult("SRT-A");await Until(()=>calls.Count==2);releases.Dequeue().SetResult("SRT-B");await queue.WaitForIdleAsync();
        Assert.Equal(new[]{"a.wav","b.wav"},calls);Assert.Equal("project",queue.JobById(first)!.ProjectId);
        var cached=queue.Enqueue("Cached",firstAudio);await queue.WaitForIdleAsync();Assert.Equal(2,calls.Count);Assert.Contains("缓存",queue.JobById(cached)!.Message);Assert.Equal("SRT-A",queue.LatestUnconsumedSrt!.Srt);
        queue.MarkSrtConsumed(cached);Assert.Null(queue.JobById(cached)!.Srt);Assert.True(queue.JobById(cached)!.SrtConsumed);await queue.FlushAsync();
        var persisted=await Store.LoadAsync();Assert.True(persisted.Single(j=>j.Id==cached).SrtConsumed);
    }
    [Fact] public async Task HoldsDuplicateDecisionAndDoesNotBlockNextJob()
    {
        var calls=0;await using var queue=Queue((_,_,_,_)=>{calls++;return Task.FromResult("SRT");},_=>Task.FromResult<DuplicateMatch?>(new(DuplicateCase.SharedAudioLesson,"Existing")));
        var first=queue.Enqueue("A",await Audio("a.wav"));var second=queue.Enqueue("B",await Audio("b.wav"));await queue.WaitForIdleAsync();
        Assert.Equal(2,queue.PendingDecisions.Count);Assert.Equal(0,calls);queue.Resolve(first,false);queue.Resolve(second,true);await queue.WaitForIdleAsync();
        Assert.Equal(TranscriptionJobStatus.Canceled,queue.JobById(first)!.Status);Assert.Equal(TranscriptionJobStatus.Completed,queue.JobById(second)!.Status);Assert.Equal(1,calls);
    }
    [Fact] public async Task CancellationAndRetryPreserveStatusAndRetainNewest()
    {
        var calls=0;Task<string> Runner(string _,IProgress<AsrProgress>? progress,CancellationToken ct,TimeSpan? estimate)
        {Interlocked.Increment(ref calls);return calls==1?Task.Delay(Timeout.Infinite,ct).ContinueWith(t=>{ct.ThrowIfCancellationRequested();return "";},ct):Task.FromResult("SRT");}
        await using var queue=Queue(Runner,retained:1);var first=queue.Enqueue("A",await Audio("a.wav"));await Until(()=>calls==1);queue.Cancel(first);await queue.WaitForIdleAsync();Assert.Equal(TranscriptionJobStatus.Canceled,queue.JobById(first)!.Status);
        queue.Retry(first);await queue.WaitForIdleAsync();Assert.Equal(TranscriptionJobStatus.Completed,queue.JobById(first)!.Status);
        var second=queue.Enqueue("B",await Audio("b.wav"));await queue.WaitForIdleAsync();Assert.Null(queue.JobById(first));Assert.Equal(TranscriptionJobStatus.Completed,queue.JobById(second)!.Status);
    }
    [Fact] public async Task RestoresInterruptedAndResumesWhileEmptySpeechUsesCache()
    {
        var audio=await Audio("silence.wav");await Store.SaveAsync([new("old","Interrupted",audio,TranscriptionJobStatus.Running,TranscriptionStage.Recognizing,"Recognizing",DateTimeOffset.UtcNow)]);
        var calls=0;await using var queue=Queue((_,_,_,_)=>{calls++;return Task.FromResult("");});await queue.RestoreAsync();Assert.Equal(1,queue.InterruptedCount);Assert.Equal(0,calls);
        queue.ResumeInterrupted();await queue.WaitForIdleAsync();Assert.Equal(1,calls);Assert.Null(queue.LatestUnconsumedSrt);Assert.Contains("没有可用语音",queue.JobById("old")!.Message);
        var second=queue.Enqueue("Again",audio);await queue.WaitForIdleAsync();Assert.Equal(1,calls);Assert.Contains("缓存",queue.JobById(second)!.Message);
    }
    [Fact] public async Task DisposalSavesActiveJobsForRecoveryAndNoFurtherNotifications()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var queue=Queue(async(_,_,ct,_)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return "";});var changes=0;queue.Changed+=(_,_)=>changes++;
        var id=queue.Enqueue("A",await Audio("a.wav"));await entered.Task;await queue.DisposeAsync();var count=changes;
        Assert.Equal(TranscriptionJobStatus.Interrupted,(await Store.LoadAsync()).Single(j=>j.Id==id).Status);Assert.Equal(count,changes);
    }
    [Fact] public async Task CacheProfileIsolationAndCompletionMetadataAreExact()
    {
        var audio=await Audio("a.wav");var calls=0;var hits=new List<bool>();
        await using(var first=new TranscriptionQueue((_,_,_,_)=>{calls++;return Task.FromResult("first");},_=>Task.FromResult<DuplicateMatch?>(null),Store,Cache,()=>"model-a"))
        {first.Completed+=(job,hit)=>{Assert.Equal("p",job.ProjectId);hits.Add(hit);};first.Enqueue("A",audio,projectId:"p");await first.WaitForIdleAsync();first.Enqueue("Again",audio,projectId:"p");await first.WaitForIdleAsync();}
        await using(var second=new TranscriptionQueue((_,_,_,_)=>{calls++;return Task.FromResult("second");},_=>Task.FromResult<DuplicateMatch?>(null),Store,Cache,()=>"model-b"))
        {second.Enqueue("B",audio);await second.WaitForIdleAsync();Assert.Equal("second",second.Jobs[0].Srt);}
        Assert.Equal(2,calls);Assert.Equal(new[]{false,true},hits);
    }
    [Fact] public async Task ImportsActualDartQueueFixtureWithRecovery()
    {
        var fixture=Path.Combine(AppContext.BaseDirectory,"fixtures","dart-queue.json");File.Copy(fixture,Store.FilePath);
        await using var queue=Queue((_,_,_,_)=>Task.FromResult(""));await queue.RestoreAsync();
        Assert.Equal(TranscriptionJobStatus.Interrupted,queue.JobById("dart-running")!.Status);Assert.Equal("dart-legacy-project",queue.JobById("dart-running")!.ProjectId);
        Assert.Equal(TimeSpan.FromSeconds(1),queue.JobById("dart-completed")!.AudioDuration);Assert.Contains("Hello from Dart",queue.JobById("dart-completed")!.Srt);Assert.Equal("{\"model\":\"qwen\"}",queue.JobById("dart-completed")!.CacheProfile);
    }
}
