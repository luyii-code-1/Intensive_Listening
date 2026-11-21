using IL.Core.Infrastructure;
using Xunit;

namespace IL.Core.Tests;

public sealed class LogFileStorageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("il-log-storage-").FullName;
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WritingToAnOversizedLegacyFileKeepsRecentLinesAndRotates()
    {
        var path = Path.Combine(_directory, "2026-11-22.log");
        using (var stream = File.Create(path))
        {
            stream.SetLength(LogFileStorage.MaxFileBytes + 256);
            stream.Position = stream.Length;
            stream.Write("\nrecent warning\n"u8);
        }
        LogFileStorage.Append(path, "新的警告");
        Assert.Equal("新的警告\n", File.ReadAllText(path));
        Assert.Equal("recent warning\n", File.ReadAllText(Path.ChangeExtension(path, ".previous.log")));
    }

    [Fact]
    public void PrunesOldestManagedFilesWithoutTouchingUnrelatedFiles()
    {
        for (var day = 1; day <= 9; day++)
        {
            var old = Path.Combine(_directory, $"2026-11-{day:D2}.log");
            using (var file = File.Create(old)) file.SetLength(LogFileStorage.MaxFileBytes);
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc));
        }
        var evidence = Path.Combine(_directory, "manual.log");
        File.WriteAllText(evidence, "keep");
        LogFileStorage.Append(Path.Combine(_directory, "2026-11-22.log"), "active");
        Assert.False(File.Exists(Path.Combine(_directory, "2026-11-01.log")));
        Assert.False(File.Exists(Path.Combine(_directory, "2026-11-02.log")));
        Assert.True(File.Exists(Path.Combine(_directory, "2026-11-03.log")));
        Assert.Equal("keep", File.ReadAllText(evidence));
        Assert.True(new DirectoryInfo(_directory).GetFiles("2026-*.log").Sum(f => f.Length) <= LogFileStorage.DirectoryBudget);
    }
}
