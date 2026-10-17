using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace IL.App.Tests;

public sealed class UpdateHelperTests
{
    [Theory]
    [InlineData(true, false, true, "--background-after-update")]
    [InlineData(false, true, true, "")]
    [InlineData(true, false, false, null)]
    public async Task InstallerCompletionStartsExpectedApplicationMode(bool background, bool reopen, bool success, string? expectedArgument)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "il-update-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "package.exe"); var executable = Path.Combine(root, "app.exe");
            await File.WriteAllBytesAsync(package, "MZ-integration-fixture"u8.ToArray()); await File.WriteAllTextAsync(executable, "fixture");
            var plan = Path.Combine(root, "plan.json");
            await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString(), version = "2.1.2", installer = package,
                sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))), size = new FileInfo(package).Length,
                processId = int.MaxValue, startedAt = DateTimeOffset.UtcNow, executable, appDirectory = root, dataDirectory = root,
                automaticOobe = false, reopen, background, feedback = true
            }));
            string Literal(string value) => "'" + value.Replace("'", "''") + "'";
            var calls = Path.Combine(root, "calls.jsonl"); var wrapper = Path.Combine(root, "run.ps1");
            // Exercise the real helper and its verification/result files; replace only process launches.
            var script = $$"""
                function Start-Process {
                    param($FilePath,$ArgumentList,$Verb,$WindowStyle,$WorkingDirectory,[switch]$PassThru,[switch]$Wait)
                    @{file=$FilePath;arguments=@($ArgumentList);verb=$Verb} | ConvertTo-Json -Compress | Add-Content -LiteralPath {{Literal(calls)}}
                    if($PassThru){ return [pscustomobject]@{ExitCode={{(success ? 0 : 5)}}} }
                }
                & {{Literal(Path.Combine(AppContext.BaseDirectory, "fixtures", "install-update.ps1"))}} -Plan {{Literal(plan)}}
                """;
            await File.WriteAllTextAsync(wrapper, script, new UTF8Encoding(true));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode == 0, await errors);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "result.json")));
            Assert.Equal(success, result.RootElement.GetProperty("success").GetBoolean());
            var launches = (await File.ReadAllLinesAsync(calls)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal("RunAs", launches[0].GetProperty("verb").GetString());
            var application = launches.Where(call => call.GetProperty("file").GetString() == executable
                && !call.GetProperty("arguments").EnumerateArray().Any(arg => arg.GetString() == "--update-feedback-only")).ToArray();
            if (expectedArgument == null) Assert.Empty(application);
            else
            {
                var launch = Assert.Single(application);
                Assert.Equal(expectedArgument, string.Join("", launch.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString())));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
