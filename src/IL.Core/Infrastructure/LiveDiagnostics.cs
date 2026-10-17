using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace IL.Core.Infrastructure;

public sealed record DiagnosticEntry(long Sequence, DateTimeOffset Time, string Level, string Category, int ThreadId, string Message)
{
    public string Preview { get; } = PreviewOf(Message);
    private static string PreviewOf(string text)
    {
        var length = Math.Min(240, text.Length);
        var newline = text.AsSpan(0, length).IndexOfAny('\r', '\n');
        if (newline >= 0) length = newline;
        return text[..length] + (length < text.Length ? " …" : "");
    }
    public override string ToString() => $"{Time:O} [{Level}] [{Category}] [T{ThreadId}] {Message}";
}

public static class LiveDiagnostics
{
    public const int Capacity = 5000;
    public const long TextBudget = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Queue<DiagnosticEntry> Entries = new();
    private static readonly Queue<DiagnosticEntry> Operations = new();
    private static long _sequence, _textBytes, _operationBytes;
    public static long LatestSequence { get { lock (Gate) return _sequence; } }
    public static DiagnosticEntry Record(string level, string category, string message)
    {
        var entry = RecordCaptured(level, category, message, DateTimeOffset.Now, Environment.CurrentManagedThreadId);
        RuntimeDiagnostics.Persist(entry);
        return entry;
    }
    internal static DiagnosticEntry RecordCaptured(string level, string category, string message, DateTimeOffset time, int thread)
    {
        // Formatting and redaction must not hold the lock used by UI snapshots.
        var redacted = CrashReportStore.Redact(message);
        lock (Gate)
        {
            var entry = new DiagnosticEntry(++_sequence, time, level, category, thread, redacted);
            Entries.Enqueue(entry); _textBytes += entry.Message.Length * 2L;
            while (Entries.Count > Capacity || Entries.Count > 1 && _textBytes > TextBudget)
                _textBytes -= Entries.Dequeue().Message.Length * 2L;
            if (category is not ("runtime" or "network" or "first-chance" or "trace"))
            {
                Operations.Enqueue(entry); _operationBytes += entry.Message.Length * 2L;
                while (Operations.Count > 200 || Operations.Count > 1 && _operationBytes > TextBudget / 2)
                    _operationBytes -= Operations.Dequeue().Message.Length * 2L;
            }
            return entry;
        }
    }
    public static IReadOnlyList<DiagnosticEntry> Snapshot() { lock (Gate) return Entries.ToArray(); }
    public static string RecentOperations()
    {
        DiagnosticEntry[] entries; lock (Gate) entries = Operations.ToArray();
        return string.Join("\n", entries.AsEnumerable());
    }
}

[Flags]
public enum DiagnosticSources { Default = 0, FirstChance = 1, NetworkDetails = 2, Runtime = 4, Trace = 8 }

public static class RuntimeDiagnostics
{
    private static readonly object Gate = new();
    private static RuntimeEvents? _events;
    private static RuntimeTrace? _trace;
    private static Session? _session, _lastSession;
    private static int _sources;
    [ThreadStatic] private static bool _recording;
    public static DiagnosticSources Sources
    {
        get => (DiagnosticSources)Volatile.Read(ref _sources);
        set
        {
            lock (Gate)
            {
                Volatile.Write(ref _sources, (int)value);
                if (_events != null) foreach (var source in EventSource.GetSources()) _events.Configure(source);
            }
        }
    }
    public static long DroppedEntries => Interlocked.Read(ref (_session ?? _lastSession ?? Empty).Dropped);
    public static long ThrottledEntries => Interlocked.Read(ref (_session ?? _lastSession ?? Empty).Throttled);
    private static readonly Session Empty = new("");
    public static string? SessionLogPath { get; private set; }
    public static Task Completion { get; private set; } = Task.CompletedTask;
    public static void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            if (enabled == (_session != null)) return;
            if (enabled)
            {
                Volatile.Write(ref _sources, 0);
                SessionLogPath = Path.Combine(AppLog.DirectoryPath, $"runtime-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
                var session = new Session(SessionLogPath);
                _session = _lastSession = session;
                Completion = Task.WhenAll(Completion, Task.Run(session.RunAsync));
                AppDomain.CurrentDomain.FirstChanceException += FirstChance;
                _trace = new(); Trace.Listeners.Add(_trace);
                _events = new();
            }
            else
            {
                var session = Interlocked.Exchange(ref _session, null)!;
                AppDomain.CurrentDomain.FirstChanceException -= FirstChance;
                _events?.Dispose(); _events = null;
                if (_trace != null) { Trace.Listeners.Remove(_trace); _trace.Dispose(); _trace = null; }
                session.Input.Writer.TryComplete();
            }
        }
    }
    internal static void Persist(DiagnosticEntry entry) => _session?.Persist(entry);
    private sealed record CaptureEntry(string Category, string Level, DateTimeOffset Time, int Thread, Func<string> Text);
    private sealed class Session(string path)
    {
        public readonly Channel<CaptureEntry> Input = Channel.CreateBounded<CaptureEntry>(512);
        private readonly Channel<DiagnosticEntry> _output = Channel.CreateBounded<DiagnosticEntry>(4096);
        private readonly object _rateGate = new();
        private readonly Dictionary<string, (long Window, int Count)> _rates = [];
        public long Dropped, Throttled;
        public bool Allow(string category)
        {
            // Bound detailed diagnostic work before constructing stacks or payload strings.
            var second = Environment.TickCount64 / 1000;
            lock (_rateGate)
            {
                var (window, count) = _rates.GetValueOrDefault(category);
                count = window == second ? count + 1 : 1;
                _rates[category] = (second, count);
                if (count <= (category == "runtime" ? 30 : 100)) return true;
            }
            Interlocked.Increment(ref Throttled); return false;
        }
        public void Persist(DiagnosticEntry entry) { if (!_output.Writer.TryWrite(entry)) Interlocked.Increment(ref Dropped); }
        public async Task RunAsync()
        {
            var writing = WriteAsync();
            try
            {
                await foreach (var entry in Input.Reader.ReadAllAsync())
                {
                    _recording = true;
                    try { Persist(LiveDiagnostics.RecordCaptured(entry.Level, entry.Category, entry.Text(), entry.Time, entry.Thread)); }
                    catch (Exception) { Interlocked.Increment(ref Dropped); }
                    finally { _recording = false; }
                }
            }
            finally { _output.Writer.TryComplete(); await writing; }
        }
        private async Task WriteAsync()
        {
            try
            {
                await using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, useAsync: true));
                while (await _output.Reader.WaitToReadAsync())
                {
                    // Bound each disk batch even when producers remain busy.
                    for (var count = 0; count < 256 && _output.Reader.TryRead(out var entry); count++)
                        await writer.WriteLineAsync(entry.ToString());
                    await writer.FlushAsync();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LiveDiagnostics.RecordCaptured("ERROR", "diagnostics", "运行时日志写入失败：" + error.Message, DateTimeOffset.Now, Environment.CurrentManagedThreadId);
                while (await _output.Reader.WaitToReadAsync()) while (_output.Reader.TryRead(out _)) Interlocked.Increment(ref Dropped);
            }
        }
    }
    private static void Capture(string category, string level, Func<string> text)
    {
        var session = _session;
        if (_recording || session == null || !session.Allow(category)) return;
        if (!session.Input.Writer.TryWrite(new(category, level, DateTimeOffset.Now, Environment.CurrentManagedThreadId, text)))
            Interlocked.Increment(ref session.Dropped);
    }
    private static void FirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (Sources.HasFlag(DiagnosticSources.FirstChance)) Capture("first-chance", "TRACE", args.Exception.ToString);
    }
    private sealed class RuntimeTrace : TraceListener
    {
        public override void Write(string? message)
        { if (Sources.HasFlag(DiagnosticSources.Trace)) Capture("trace", "TRACE", () => message ?? ""); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class RuntimeEvents : EventListener
    {
        protected override void OnEventSourceCreated(EventSource source) => Configure(source);
        public void Configure(EventSource source)
        {
            if (source.Name == "System.Runtime" || source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                if (!Sources.HasFlag(DiagnosticSources.Runtime)) { DisableEvents(source); return; }
                if (source.Name == "System.Runtime")
                    EnableEvents(source, EventLevel.Informational, EventKeywords.All, new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
                else EnableEvents(source, EventLevel.Informational, (EventKeywords)0x10001);
            }
            else if (source.Name is "System.Net.Http" or "System.Net.Sockets" or "System.Net.NameResolution" or "System.Net.Security")
                EnableEvents(source, Sources.HasFlag(DiagnosticSources.NetworkDetails) ? EventLevel.Informational : EventLevel.Warning, EventKeywords.All);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            var network = args.EventSource.Name.StartsWith("System.Net.", StringComparison.Ordinal);
            var failed = args.EventName?.Contains("Fail", StringComparison.OrdinalIgnoreCase) == true || args.Level is EventLevel.Error or EventLevel.Critical;
            if (network && !failed && !Sources.HasFlag(DiagnosticSources.NetworkDetails)) return;
            if (!network && !Sources.HasFlag(DiagnosticSources.Runtime)) return;
            Capture(network ? "network" : "runtime", failed ? "WARN" : "TRACE", () =>
                args.EventSource.Name + "/" + args.EventName + " " + string.Join(" ", (args.Payload ?? []).Select((value, index) =>
                    (args.PayloadNames?.ElementAtOrDefault(index) ?? index.ToString()) + "=" + Format(value))));
        }
        private static string Format(object? value) => value is IDictionary<string, object> fields
            ? string.Join(", ", fields.Select(pair => pair.Key + "=" + pair.Value)) : value?.ToString() ?? "null";
    }
}
