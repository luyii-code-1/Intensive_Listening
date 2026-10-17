using IL.App.Services;
using IL.Core.Infrastructure;
using Xunit;

namespace IL.App.Tests;

[CollectionDefinition("Diagnostics", DisableParallelization = true)]
public sealed class DiagnosticsCollection { }

[Collection("Diagnostics")]
public sealed class DiagnosticsTests
{
    [Fact]
    public async Task RuntimeStreamIncludesCaughtExceptionsAndEventPipeProducesNettrace()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = AppLog.DirectoryOverride;
        AppLog.DirectoryOverride = root;
        try
        {
            AppLog.DebugEnabled = true;
            RuntimeDiagnostics.Sources = DiagnosticSources.FirstChance | DiagnosticSources.Runtime;
            try { throw new InvalidOperationException("caught-diagnostics-marker"); } catch (InvalidOperationException) { }
            AppLog.Operation("authoring", "start transcription diagnostic-marker");
            await Until(() => LiveDiagnostics.Snapshot().Any(entry => entry.Category == "first-chance" && entry.Message.Contains("caught-diagnostics-marker")));
            var path = Path.Combine(root, "sampling.nettrace");
            await using (var recorder = new EventPipeRecorder())
            {
                await recorder.StartAsync(path);
                Assert.True(recorder.IsRecording);
                // Exercise the runtime long enough to collect thread samples and rundown metadata.
                await Task.Run(() => { var timer = System.Diagnostics.Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < 150) Thread.SpinWait(1000); });
                await recorder.StopAsync();
                Assert.False(recorder.IsRecording);
            }
            GC.Collect();
            await Until(() => LiveDiagnostics.Snapshot().Any(entry => entry.Category == "runtime"));
            Assert.True(new FileInfo(path).Length > 100);
            var header = new byte[8]; using (var file = File.OpenRead(path)) file.ReadExactly(header);
            Assert.Equal("Nettrace", System.Text.Encoding.ASCII.GetString(header));
        }
        finally
        {
            AppLog.DebugEnabled = false;
            await RuntimeDiagnostics.Completion;
            var log = await File.ReadAllTextAsync(RuntimeDiagnostics.SessionLogPath!);
            Assert.Contains("caught-diagnostics-marker", log);
            Assert.Contains("start transcription diagnostic-marker", log);
            AppLog.DirectoryOverride = previous;
            Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    [Fact]
    public async Task DefaultSourcesExcludeTraceNoiseAndCaughtExceptionsButKeepApplicationErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-diagnostics-default-" + Guid.NewGuid().ToString("N"));
        var previous = AppLog.DirectoryOverride; AppLog.DirectoryOverride = root;
        var marker = Guid.NewGuid().ToString("N");
        try
        {
            AppLog.DebugEnabled = true;
            Assert.Equal(DiagnosticSources.Default, RuntimeDiagnostics.Sources);
            System.Diagnostics.Trace.WriteLine("routine-trace-" + marker);
            try { throw new InvalidOperationException("caught-routine-" + marker); } catch (InvalidOperationException) { }
            await AppLog.WriteAsync("ERROR", "application-error-" + marker, new IOException("important-inner"));
            await Task.Delay(100);
            Assert.DoesNotContain(LiveDiagnostics.Snapshot(), entry => entry.Message.Contains(marker) && entry.Category is "trace" or "first-chance");
            Assert.Contains(LiveDiagnostics.Snapshot(), entry => entry.Level == "ERROR" && entry.Message.Contains("important-inner"));
        }
        finally
        {
            AppLog.DebugEnabled = false; await RuntimeDiagnostics.Completion;
            AppLog.DirectoryOverride = previous; Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task DetailedTraceFloodIsLimitedBeforeEnteringTheLiveBuffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-diagnostics-flood-" + Guid.NewGuid().ToString("N"));
        var previous = AppLog.DirectoryOverride; AppLog.DirectoryOverride = root;
        var marker = "trace-flood-" + Guid.NewGuid().ToString("N");
        try
        {
            AppLog.DebugEnabled = true; RuntimeDiagnostics.Sources = DiagnosticSources.Trace;
            for (var index = 0; index < 20000; index++) System.Diagnostics.Trace.WriteLine(marker);
            Assert.True(RuntimeDiagnostics.ThrottledEntries > 0);
        }
        finally
        {
            AppLog.DebugEnabled = false; await RuntimeDiagnostics.Completion;
            var log = await File.ReadAllTextAsync(RuntimeDiagnostics.SessionLogPath!);
            Assert.Contains(marker, log);
            AppLog.DirectoryOverride = previous; Directory.Delete(root, true);
        }
    }
    private sealed class BlockingDiagnosticException(ManualResetEventSlim release, string marker) : Exception
    {
        public override string ToString() { release.Wait(TimeSpan.FromSeconds(10)); return marker; }
    }
    [Fact]
    public async Task ExceptionFormattingDoesNotBlockTheThrowingThreadAndPreservesItsIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-diagnostics-async-" + Guid.NewGuid().ToString("N"));
        var previous = AppLog.DirectoryOverride; AppLog.DirectoryOverride = root;
        using var release = new ManualResetEventSlim(false);
        var marker = "deferred-stack-" + Guid.NewGuid().ToString("N");
        Task<int>? producer = null;
        try
        {
            AppLog.DebugEnabled = true; RuntimeDiagnostics.Sources = DiagnosticSources.FirstChance;
            producer = Task.Run(() =>
            {
                var thread = Environment.CurrentManagedThreadId;
                try { throw new BlockingDiagnosticException(release, marker); } catch (BlockingDiagnosticException) { }
                return thread;
            });
            var origin = await producer.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await Until(() => LiveDiagnostics.Snapshot().Any(entry => entry.Message == marker));
            Assert.Contains(LiveDiagnostics.Snapshot(), entry => entry.Message == marker && entry.ThreadId == origin);
        }
        finally
        {
            release.Set(); if (producer != null) await producer;
            AppLog.DebugEnabled = false; await RuntimeDiagnostics.Completion;
            AppLog.DirectoryOverride = previous; Directory.Delete(root, true);
        }
    }
    [Fact]
    public void BufferAndViewRemainBoundedUnderLargeMessages()
    {
        var message = new string('x', 16000) + "\ncomplete-tail";
        for (var index = 0; index < 2000; index++) LiveDiagnostics.Record("INFO", "fixture", message);
        var snapshot = LiveDiagnostics.Snapshot();
        Assert.True(snapshot.Sum(entry => entry.Message.Length * 2L) <= LiveDiagnostics.TextBudget);
        Assert.All(snapshot, entry => { Assert.EndsWith("complete-tail", entry.Message); Assert.True(entry.Preview.Length <= 242); });
        var input = Enumerable.Range(1, 5000).Select(index => new DiagnosticEntry(index, DateTimeOffset.Now, "INFO", "fixture", 1, "record " + index)).ToArray();
        var initial = DiagnosticLogView.Read(input, "", null, null, 0, 0, true);
        Assert.Equal(1000, initial.Entries.Length); Assert.Equal(4001, initial.Entries[0].Sequence);
        var next = DiagnosticLogView.Read(input, "", null, null, 0, 100, false);
        Assert.Equal(200, next.Entries.Length); Assert.Equal(4700, next.Skipped); Assert.Equal(5000, next.Latest);
        var searched = DiagnosticLogView.Read(input, "record 2500", null, null, 0, 0, true);
        Assert.Equal(2500, Assert.Single(searched.Entries).Sequence);
    }

}
