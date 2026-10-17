using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using IL.Core.Infrastructure;

namespace IL.App.Services;

public sealed class EventPipeRecorder : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EventPipeSession? _session;
    private Task? _copy;
    private FileStream? _output;
    public string? Path { get; private set; }
    public bool IsRecording => _session != null;
    public async Task StartAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session != null) return;
            if (!AppLog.DebugEnabled) throw new InvalidOperationException("请先开启调试模式。");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var client = new DiagnosticsClient(Environment.ProcessId);
            var session = await client.StartEventPipeSessionAsync([
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x18001),
                new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
            ], requestRundown: true, circularBufferMB: 32, token: deadline.Token);
            try { _output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, useAsync: true); }
            catch { try { await session.StopAsync(deadline.Token); } finally { session.Dispose(); } throw; }
            _session = session; Path = path;
            _copy = session.EventStream.CopyToAsync(_output);
            AppLog.Operation("diagnostics", "开始 EventPipe 调用栈采样");
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_session == null) return;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _session.StopAsync(deadline.Token);
                if (_copy != null) await _copy.WaitAsync(deadline.Token);
                if (_output != null) await _output.FlushAsync(deadline.Token);
                AppLog.Operation("diagnostics", "EventPipe 采样已保存");
            }
            finally { _session.Dispose(); _session = null; _output?.Dispose(); _output = null; _copy = null; }
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
