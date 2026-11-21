using IL.Core.Infrastructure;
using Xunit;

namespace IL.App.Tests;

[Collection("Diagnostics")]
public sealed class RuntimeLogRotationTests
{
    [Fact]
    public async Task SessionRotatesAndFlushesLastEntryOnShutdown()
    {
        var directory = Directory.CreateTempSubdirectory("il-session-rotation-");
        var previousDirectory = AppLog.DirectoryOverride;
        AppLog.DirectoryOverride = directory.FullName;
        try
        {
            AppLog.DebugEnabled = true;
            var path = RuntimeDiagnostics.SessionLogPath!;
            var text = new string('x', 16 * 1024);
            LiveDiagnostics.Record("INFO", "test", "before-rotation");
            for (var i = 0; i < 600; i++) LiveDiagnostics.Record("INFO", "test", text);
            LiveDiagnostics.Record("INFO", "test", "after-rotation");
            AppLog.DebugEnabled = false;
            await RuntimeDiagnostics.Completion;

            var archive = Path.ChangeExtension(path, ".previous.log");
            Assert.Contains(File.ReadLines(archive), line => line.Contains("before-rotation"));
            Assert.Contains(File.ReadLines(path), line => line.Contains("after-rotation"));
            Assert.All(new[] { path, archive }, file => Assert.InRange(new FileInfo(file).Length, 1L, 8L * 1024 * 1024));
        }
        finally
        {
            AppLog.DebugEnabled = false;
            await RuntimeDiagnostics.Completion;
            AppLog.DirectoryOverride = previousDirectory;
            directory.Delete(recursive: true);
        }
    }
}
