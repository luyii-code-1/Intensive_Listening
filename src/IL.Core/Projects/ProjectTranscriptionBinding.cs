using System.Text;
using IL.Core.Ilp;
using IL.Core.Infrastructure;
using IL.Core.Transcription;
using IL.Core.Audio;

namespace IL.Core.Projects;

/// <summary>Persists completed subtitles independently of the visible authoring page.</summary>
public sealed class ProjectTranscriptionBinding : IAsyncDisposable
{
    private readonly CourseProjectStore _store;
    private readonly TranscriptionQueue _queue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _workGate = new();
    private Task _work = Task.CompletedTask;
    private readonly Func<string, Task<TimeSpan?>> _probeDuration;
    public event Action<CourseProject>? TranscriptBound;
    public ProjectTranscriptionBinding(CourseProjectStore store, TranscriptionQueue queue, Func<string, Task<TimeSpan?>>? probeDuration = null)
    { _store = store; _queue = queue; _probeDuration = probeDuration ?? (path => VadSlicer.ProbeDurationAsync(path)); queue.Completed += Completed; }
    public async Task<CourseProject> EnqueueAsync(CourseProject project)
    {
        // Cached results can finish immediately, so the project link must exist first.
        var id = Guid.NewGuid().ToString("N");
        var duration = await _probeDuration(project.AudioPath!);
        var updated = project with { TranscriptionJobId = id, Step = CourseProjectStep.Transcription, AutoQuestionPlanDeferred = false,
            AudioDuration = duration > TimeSpan.Zero ? duration : project.AudioDuration };
        await _store.SaveAsync(updated);
        AppLog.Operation("transcription", $"提交转写 project={project.Id} job={id} duration={updated.AudioDuration}");
        _queue.Enqueue(updated.Title, updated.AudioPath!, updated.AudioDuration, updated.Id, id);
        return updated;
    }
    private void Completed(TranscriptionJob job, bool cacheHit)
    {
        lock (_workGate) _work = _work.ContinueWith(async _ =>
        {
            try { await BindAsync(job); }
            catch (Exception e) { AppLog.Error("转写字幕绑定失败，下次打开工程重试", e); }
        }, TaskScheduler.Default).Unwrap();
    }
    public Task FlushAsync() { lock (_workGate) return _work; }
    public async Task RestorePendingAsync()
    {
        await FlushAsync();
        foreach (var job in _queue.Jobs.Where(j => j.Status == TranscriptionJobStatus.Completed && !j.SrtConsumed))
        {
            try { await BindAsync(job); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { AppLog.Error($"工程 {job.ProjectId} 的转写字幕恢复失败，保留结果待重试", e); }
        }
    }
    public async Task<CourseProject?> BindAsync(TranscriptionJob job)
    {
        if (job.ProjectId == null || job.SrtConsumed || string.IsNullOrWhiteSpace(job.Srt)) return null;
        await _gate.WaitAsync();
        try
        {
            var duration = await _probeDuration(job.AudioPath);
            var updated = await _store.UpdateAsync(job.ProjectId, project =>
            {
                if (project == null || project.TranscriptionJobId != job.Id) return null;
                if (duration > TimeSpan.Zero && project.AudioPath == job.AudioPath) project = project with { AudioDuration = duration };
                var cues = SrtParser.Parse(Encoding.UTF8.GetBytes(job.Srt), project.AudioDuration ?? TimeSpan.FromDays(7));
                if (cues.Count == 0) return null;
                return project with { Transcript = job.Srt, TranscriptionJobId = null, Step = CourseProjectStep.Review,
                    ReviewPhase = ReviewPhase.Grouping, AutomaticQuestionPlanApplied = false, AutoQuestionPlanDeferred = false, UpdatedAt = DateTimeOffset.Now };
            });
            if (updated == null) return null;
            _queue.MarkSrtConsumed(job.Id);
            AppLog.Operation("transcription", $"字幕绑定完成 project={updated.Id} job={job.Id} characters={job.Srt.Length}");
            TranscriptBound?.Invoke(updated);
            return updated;
        }
        catch (IlpException e)
        {
            _queue.FailSrtBinding(job.Id, e.Message);
            AppLog.Error($"工程 {job.ProjectId} 的转写字幕校验失败，原工程与识别结果已保留", e);
            return null;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { _queue.Completed -= Completed; await FlushAsync(); }
}
