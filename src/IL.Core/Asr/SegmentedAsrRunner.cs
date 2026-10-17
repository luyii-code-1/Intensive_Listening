using System.Text;
using System.Runtime.ExceptionServices;
using IL.Core.Audio;
using IL.Core.Infrastructure;
using IL.Core.Ilp;
namespace IL.Core.Asr;
public sealed class SegmentedAsrRunner(AsrConfig config,int concurrency,TimeSpan timeout,VadSlicer? slicer=null,AsrClient? client=null)
{
    public const int MaxRequestsPerSecond=10;
    public async Task<string> RunAsync(string audioFile,IProgress<AsrProgress>? progress=null,CancellationToken ct=default,Func<Task<bool>>? confirmForcedCuts=null)
    {
        var temp=Path.Combine(Path.GetTempPath(),"ilp-asr-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        using var workersCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            progress?.Report(new(AsrStage.Decoding));progress?.Report(new(AsrStage.Slicing));var actualSlicer=slicer??new VadSlicer();VadSliceResult sliced;
            try{sliced=await actualSlicer.SliceAsync(audioFile,temp,ct:ct);}
            catch(NoSilenceCutException){if(confirmForcedCuts==null||!await confirmForcedCuts())throw new OperationCanceledException(ct);sliced=await actualSlicer.SliceAsync(audioFile,temp,forceCutOnNoSilence:true,ct:ct);}
            var results=new IReadOnlyList<SrtCue>[sliced.Slices.Count];var next=-1;var completed=0;var gate=new SemaphoreSlim(1,1);var nextStart=DateTimeOffset.MinValue;
            Exception? firstFailure = null;
            async Task Worker()
            {
                try
                {
                    while(true)
                    {
                        var index=Interlocked.Increment(ref next);if(index>=sliced.Slices.Count)return;
                        await gate.WaitAsync(workersCancellation.Token);
                        try{var wait=nextStart-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait,workersCancellation.Token);nextStart=DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(100);}
                        finally{gate.Release();}
                        var slice=sliced.Slices[index]; string srt;
                        AppLog.Operation("asr", $"识别分段 index={index+1}/{sliced.Slices.Count} startMs={slice.Start.TotalMilliseconds:F0} endMs={slice.End.TotalMilliseconds:F0}");
                        try { srt=await (client??new AsrClient(timeout:timeout)).TranscribeToSrtAsync(config,slice.File,ct:workersCancellation.Token); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        { throw new AsrException($"第 {index+1}/{sliced.Slices.Count} 段识别失败：{error.Message}", error); }
                        var cues=string.IsNullOrWhiteSpace(srt)?[]:SrtParser.Parse(Encoding.UTF8.GetBytes(srt),slice.End-slice.Start+TimeSpan.FromSeconds(1));
                        results[index]=cues.Select(c=>c with{Start=c.Start+slice.Start,End=c.End+slice.Start}).ToArray();
                        var done=Interlocked.Increment(ref completed);progress?.Report(new(AsrStage.Recognizing,(double)done/results.Length,SegmentIndex:done,SegmentTotal:results.Length));
                    }
                }
                catch (Exception error)
                {
                    if (error is not OperationCanceledException) Interlocked.CompareExchange(ref firstFailure, error, null);
                    workersCancellation.Cancel(); throw;
                }
            }
            try { await Task.WhenAll(Enumerable.Range(0,Math.Min(Math.Clamp(concurrency,1,10),sliced.Slices.Count)).Select(_=>Worker())); }
            catch
            {
                ct.ThrowIfCancellationRequested();
                if (firstFailure != null) ExceptionDispatchInfo.Capture(firstFailure).Throw();
                throw;
            }
            finally { gate.Dispose(); }
            progress?.Report(new(AsrStage.Merging,SegmentIndex:results.Length,SegmentTotal:results.Length));
            return SrtParser.Serialize(Normalize(results.SelectMany(r=>r))).TrimEnd();
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    public static IReadOnlyList<SrtCue> Normalize(IEnumerable<SrtCue> source)
    {
        var result=new List<SrtCue>();foreach(var cue in source.OrderBy(c=>c.Start)){var start=result.Count==0||cue.Start>=result[^1].End?cue.Start:result[^1].End;if(cue.End>start&&!string.IsNullOrWhiteSpace(cue.Text))result.Add(cue with{Start=start,Text=cue.Text.Trim()});}return result;
    }
}
