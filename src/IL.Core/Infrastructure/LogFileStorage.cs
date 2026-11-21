using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IL.Core.Infrastructure;

internal static partial class LogFileStorage
{
    internal const int MaxFileBytes = 8 * 1024 * 1024;
    internal const long DirectoryBudget = 64L * 1024 * 1024;
    private const int MaxFiles = 64;
    private const int MaxRecordChars = 32 * 1024;

    [GeneratedRegex(@"^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}|runtime-[0-9]{8}-[0-9]{6}-[a-fA-F0-9]{32})(?:\.previous)?\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedName();

    internal static string LimitRecord(string text, int limit = MaxRecordChars)
    {
        if (text.Length <= limit) return text;
        const string marker = "\n[…日志已截短…]\n";
        var head = (limit - marker.Length) / 2;
        var tail = text.Length - (limit - marker.Length - head);
        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (char.IsLowSurrogate(text[tail])) tail++;
        return string.Concat(text.AsSpan(0, head), marker, text.AsSpan(tail));
    }

    public static void Append(string path, string text) => Append(path, [text]);

    public static void Append(string path, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(path)!;
        WithDirectoryLock(directory, () =>
        {
            FileStream stream = OpenAppend(path);
            try
            {
                var length = stream.Length;
                foreach (var line in lines)
                {
                    var bytes = Encoding.UTF8.GetBytes(LimitRecord(line, MaxRecordChars + 256) + "\n");
                    if (length + bytes.Length > MaxFileBytes)
                    {
                        stream.Dispose();
                        Rotate(path, length);
                        stream = OpenAppend(path);
                        length = 0;
                    }
                    stream.Write(bytes);
                    length += bytes.Length;
                }
            }
            finally { stream.Dispose(); }
            Prune(directory, path);
            return true;
        });
    }

    private static FileStream OpenAppend(string path) => new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    private static void Rotate(string path, long length)
    {
        var previous = Path.ChangeExtension(path, ".previous.log");
        if (length <= MaxFileBytes)
        {
            File.Move(path, previous, overwrite: true);
            return;
        }

        // Older versions could leave today's file hundreds of MiB long. Keep its recent complete lines.
        var tail = new byte[MaxFileBytes];
        using (var source = File.OpenRead(path))
        {
            source.Seek(-MaxFileBytes, SeekOrigin.End);
            source.ReadExactly(tail);
        }
        var first = Array.IndexOf(tail, (byte)'\n') + 1;
        var temporary = previous + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                if (first != 0) output.Write(tail.AsSpan(first));
            File.Move(temporary, previous, overwrite: true);
            File.Delete(path);
        }
        finally { File.Delete(temporary); }
    }

    // Both the main app and standalone players may write to a directory; serialize rotation and pruning.
    internal static T WithDirectoryLock<T>(string directory, Func<T> action)
    {
        var normalized = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        using var mutex = new Mutex(false, "IntensiveListening.Logs." + key);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Ownership passes to this writer. */ }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Prune(string directory, string active)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.log")
            .Where(file => OwnedName().IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var bytes = files.Sum(file => file.Length);
        var count = files.Length;
        foreach (var file in files)
        {
            if (bytes <= DirectoryBudget && count <= MaxFiles) break;
            if (file.FullName == active) continue;
            try
            {
                var length = file.Length;
                file.Delete();
                bytes -= length;
                count--;
            }
            catch (IOException) { /* A previous app version or a reader may still hold this file. */ }
            catch (UnauthorizedAccessException) { /* Continue with other eligible logs. */ }
        }
    }
}
