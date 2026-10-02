using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IL.Core.Infrastructure;

public sealed record CrashReport
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string AppVersion { get; init; } = "2.0.0";
    public string Platform { get; init; } = RuntimeInformation.OSDescription;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public int ProcessId { get; init; } = Environment.ProcessId;
    public string Component { get; init; } = "主程序";
    public string ExceptionType { get; init; } = "UnexpectedProcessExit";
    public string Message { get; init; } = "程序意外退出，未能获得托管异常堆栈。";
    public string Details { get; init; } = "";
    public int? ExitCode { get; init; }
    public bool Fatal { get; init; } = true;
    public bool UploadAllowed { get; init; }
    public bool Displayed { get; init; }
    public DateTimeOffset? AcceptedByTelemetryAt { get; init; }
    public static CrashReport FromException(Exception exception, string component, bool fatal, bool uploadAllowed) => new()
    { ExceptionType = exception.GetType().FullName ?? exception.GetType().Name, Message = CrashReportStore.Redact(exception.Message), Details = CrashReportStore.Redact(exception.ToString()), Component = component, Fatal = fatal, UploadAllowed = uploadAllowed };
    public string Summary => $"时间：{OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n版本：{AppVersion}\n平台：{Platform} ({Architecture})\n组件：{Component}\n问题：{ExceptionType}\n{Message}\n\n{Details}\n\n报告 ID：{Id}";
}

/// <summary>Local, atomic reports remain available even when logging or the UI dispatcher has failed.</summary>
public sealed class CrashReportStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath => Path.Combine(root, "crash-reports");
    public string PathFor(string id) => Path.Combine(DirectoryPath, id + ".json");
    public void Save(CrashReport report)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(report.Id); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(report, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static CrashReport? Load(string path)
    {
        try { return JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public IReadOnlyList<CrashReport> Reports() => !Directory.Exists(DirectoryPath) ? [] : Directory.EnumerateFiles(DirectoryPath, "*.json").Select(Load).OfType<CrashReport>().OrderBy(r => r.OccurredAt).ToArray();
    public async Task FlushAsync(bool consent, Func<CrashReport, Task<bool>> send)
    {
        foreach (var report in Reports().Where(r => r.UploadAllowed && r.AcceptedByTelemetryAt == null))
        {
            try
            {
                using var uploadLock = new FileStream(PathFor(report.Id) + ".upload.lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                var latest = Load(PathFor(report.Id));
                if (latest == null || !latest.UploadAllowed || latest.AcceptedByTelemetryAt != null) continue;
                if (!consent) { Save(latest with { UploadAllowed = false }); continue; }
                if (await send(latest)) Save((Load(PathFor(report.Id)) ?? latest) with { AcceptedByTelemetryAt = DateTimeOffset.UtcNow });
            }
            catch (IOException) { /* Another instance is already submitting this report. */ }
            catch (Exception e) { AppLog.Warning("崩溃报告暂未送达，下次启动重试", e); }
        }
    }
    // Exception text may include request URLs or authorization headers. Never attach settings or lesson content.
    public static string Redact(string text) => Regex.Replace(Regex.Replace(text, @"(?i)(authorization\s*[:=]\s*(?:bearer\s+)?|api[_-]?key\s*[:=]\s*|[?&](?:key|token|api[_-]?key)=)[^\s&\""']+", "$1[redacted]"), @"\bsk-[A-Za-z0-9_-]{8,}\b", "[redacted]");
}

public sealed record CrashSession(string Id, int ProcessId, string Root, DateTimeOffset StartedAt, bool CleanExit = false, bool UploadAllowed = false, string? ReportId = null);
