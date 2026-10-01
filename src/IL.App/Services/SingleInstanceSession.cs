using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Infrastructure;

namespace IL.App.Services;

/// <summary>One resident workspace per user, with file-open requests forwarded to it.</summary>
public sealed class SingleInstanceSession : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<string[]> _pending = new();
    private readonly Task _listener;
    private Action<string[]>? _activate;
    public static string ApplicationName => "IntensiveListening.2." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppDirectories.DataDirectory().ToLowerInvariant())))[..16];
    private SingleInstanceSession(Mutex mutex, string name)
    { _mutex = mutex; _name = name; _listener = Task.Run(ListenAsync); }
    public static SingleInstanceSession? Acquire(string name)
    {
        var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Local\\" : "") + name);
        bool owned; try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (owned) return new(mutex, name);
        mutex.Dispose(); return null;
    }
    public void SetActivationHandler(Action<string[]> activate)
    {
        string[][] pending;
        lock (_gate) { _activate = activate; pending = _pending.ToArray(); _pending.Clear(); }
        foreach (var args in pending) activate(args);
    }
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line == null) continue;
                var args = JsonSerializer.Deserialize<string[]>(line) ?? [];
                Action<string[]>? activate;
                lock (_gate) { activate = _activate; if (activate == null) _pending.Enqueue(args); }
                activate?.Invoke(args);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or JsonException)
            {
                if (_stop.IsCancellationRequested) break;
                AppLog.Warning("接收窗口唤回请求失败", error);
                try { await Task.Delay(50, _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public static async Task<bool> NotifyAsync(string name, string[] args)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(deadline.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(args).AsMemory(), deadline.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
        { AppLog.Warning("常驻实例未响应唤回请求", error); return false; }
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.GetAwaiter().GetResult(); _stop.Dispose();
        _mutex.ReleaseMutex(); _mutex.Dispose();
    }
}
