using System.Text;
using IL.Core.Ilp;
using IL.Core.Infrastructure;
using IL.Core.Transcription;

namespace IL.Core.Projects;

/// <summary>Persists completed subtitles independently of the visible authoring page.</summary>
public sealed class ProjectTranscriptionBinding : IAsyncDisposable
{
    private readonly CourseProjectStore _store;
    private readonly TranscriptionQueue _queue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _workGate = new();
    private Task _work = Task.CompletedTask;
    public event Action<CourseProject>? TranscriptBound;
    public ProjectTranscriptionBinding(CourseProjectStore store, TranscriptionQueue queue)
    { _store = store; _queue = queue; queue.Completed += Completed; }
    public async Task<CourseProject> EnqueueAsync(CourseProject project)
    {
        // Cached results can finish immediately, so the project link must exist first.
        var id = Guid.NewGuid().ToString("N");
        var updated = project with { TranscriptionJobId = id, Step = CourseProjectStep.Transcription, AutoQuestionPlanDeferred = false };
        await _store.SaveAsync(updated);
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
        foreach (var job in _queue.Jobs.Where(j => j.Status == TranscriptionJobStatus.Completed && !j.SrtConsumed)) await BindAsync(job);
    }
    public async Task<CourseProject?> BindAsync(TranscriptionJob job)
    {
        if (job.ProjectId == null || job.SrtConsumed || string.IsNullOrWhiteSpace(job.Srt)) return null;
        await _gate.WaitAsync();
        try
        {
            var project = await _store.LoadByIdAsync(job.ProjectId);
            if (project == null || project.TranscriptionJobId != job.Id) return null;
            var cues = SrtParser.Parse(Encoding.UTF8.GetBytes(job.Srt), project.AudioDuration ?? TimeSpan.FromDays(7));
            if (cues.Count == 0) return null;
            var updated = project with { Transcript = job.Srt, TranscriptionJobId = null, Step = CourseProjectStep.Review,
                ReviewPhase = ReviewPhase.Grouping, AutomaticQuestionPlanApplied = false, AutoQuestionPlanDeferred = false, UpdatedAt = DateTimeOffset.Now };
            await _store.SaveAsync(updated);
            _queue.MarkSrtConsumed(job.Id);
            TranscriptBound?.Invoke(updated);
            return updated;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { _queue.Completed -= Completed; await FlushAsync(); }
}
