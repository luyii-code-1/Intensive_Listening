using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IL.App.Services;
using Xunit;

namespace IL.App.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-update-中文 空格-" + Guid.NewGuid().ToString("N"));
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(3010, false, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public async Task WindowsPowerShellRunsHelperAndRecordsInstallAndRestart(int exitCode, bool rejectUac, bool success)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunHelperAsync(exitCode, rejectUac);
        Assert.Equal(success, result.GetProperty("success").GetBoolean());
        var calls = File.ReadAllLines(Path.Combine(_root, "calls.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Assert.DoesNotContain(calls, c => c.GetProperty("kind").GetString() == "notice");
        var install = Assert.Single(calls, c => c.GetProperty("kind").GetString() == "install");
        Assert.Equal("RunAs", install.GetProperty("verb").GetString());
        Assert.EndsWith("Intensive Listening更新.exe", install.GetProperty("path").GetString());
        var args = install.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Contains("/MDNUPDATE=1", args);
        Assert.Contains($"/DIR=\"{_root}\"", args);
        Assert.Contains($"/MDNDATA=\"{_root}\"", args);
        Assert.Contains(args, a => a!.StartsWith("/LOG="));
        Assert.Single(calls, c => c.GetProperty("kind").GetString() == "restart");
        Assert.True(File.Exists(Path.Combine(_root, "ready")));
        Assert.Contains("Helper starting", File.ReadAllText(Path.Combine(_root, "install-update.log")));
    }

    [Fact]
    public async Task InvalidHashNeverLaunchesInstallerAndRestartsAppWithFailureResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunHelperAsync(0, false, invalidHash: true);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("安装包校验失败。", result.GetProperty("message").GetString());
        var calls = File.ReadAllText(Path.Combine(_root, "calls.jsonl"));
        Assert.DoesNotContain("install", calls); Assert.Contains("restart", calls);
    }

    [Fact]
    public async Task InvalidPlanWritesFailureAndNeverSignalsReady()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunHelperAsync(0, false, invalidPlan: true);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_root, "ready")));
        Assert.False(File.Exists(Path.Combine(_root, "calls.jsonl")));
    }

    [Fact]
    public async Task LauncherRequiresHelperAcknowledgement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(_root, "stub.ps1");
        await File.WriteAllTextAsync(helper, "param($Plan,$Ready)\n[IO.File]::WriteAllText($Ready,'ready')\n", new UTF8Encoding(true));
        var ready = Path.Combine(_root, "ready");
        await ManagedUpdater.StartHelperAsync(helper, Path.Combine(_root, "plan.json"), ready, CancellationToken.None);
        Assert.False(File.Exists(ready));
    }

    [Fact]
    public async Task LauncherReportsScriptFailureBeforeApplicationExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(_root, "stub.ps1");
        await File.WriteAllTextAsync(helper, "param($Plan,$Ready)\nthrow 'helper failed'\n");
        await Assert.ThrowsAsync<IOException>(() => ManagedUpdater.StartHelperAsync(helper, Path.Combine(_root, "plan.json"), Path.Combine(_root, "ready"), CancellationToken.None));
        Assert.Contains("helper failed", await File.ReadAllTextAsync(Path.Combine(_root, "install-update.log")));
    }

    [Fact]
    public async Task LauncherStopsUnresponsiveHelper()
    {
        if (!OperatingSystem.IsWindows()) return;
        var helper = Path.Combine(_root, "stub.ps1");
        await File.WriteAllTextAsync(helper, "param($Plan,$Ready)\nStart-Sleep -Seconds 20\n");
        await Assert.ThrowsAsync<TimeoutException>(() => ManagedUpdater.StartHelperAsync(helper, Path.Combine(_root, "plan.json"), Path.Combine(_root, "ready"), CancellationToken.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DeferredUpdateDoesNotReopenWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunHelperAsync(0, false, reopen: false);
        Assert.True(result.GetProperty("success").GetBoolean());
        var calls = File.ReadAllLines(Path.Combine(_root, "calls.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Assert.Single(calls, c => c.GetProperty("kind").GetString() == "install");
        Assert.DoesNotContain(calls, c => c.GetProperty("kind").GetString() == "restart");
    }
    private async Task<JsonElement> RunHelperAsync(int exitCode, bool rejectUac, bool invalidHash = false, bool invalidPlan = false, bool reopen = true)
    {
        var package = Path.Combine(_root, "安装包.exe");
        await File.WriteAllBytesAsync(package, [0x4d, 0x5a, 0x01]);
        var plan = Path.Combine(_root, "plan.json");
        await File.WriteAllTextAsync(plan, invalidPlan ? "{ invalid json" : JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(), version = "2.0.1", organization = "测试组织", installer = package,
            sha256 = invalidHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))),
            size = 3, processId = int.MaxValue, startedAt = DateTime.UtcNow, executable = package,
            appDirectory = _root, dataDirectory = _root, automaticOobe = true, reopen
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var wrapper = Path.Combine(_root, "wrapper.ps1");
        await File.WriteAllTextAsync(wrapper, """
            param($Helper,$Plan,$Ready)
            function Start-Process {
                param($FilePath,$ArgumentList,$Verb,$WindowStyle,[switch]$PassThru,[switch]$Wait,$WorkingDirectory)
                $kind = if ($PassThru) { 'install' } else { 'restart' }
                @{kind=$kind;path=$FilePath;args=$ArgumentList;verb=$Verb} | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:IL_TEST_CALLS -Encoding UTF8
                if ($PassThru) {
                    if ($env:IL_TEST_REJECT -eq 'True') { throw 'UAC cancelled' }
                    return [pscustomobject]@{ExitCode=[int]$env:IL_TEST_EXIT}
                }
            }
            function New-Object {
                param($TypeName,$ArgumentList)
                if ($TypeName -ne 'System.Windows.Forms.NotifyIcon') { return Microsoft.PowerShell.Utility\New-Object @PSBoundParameters }
                $item = [pscustomobject]@{Icon=$null;Text='';Visible=$false}
                $item | Add-Member ScriptMethod ShowBalloonTip {
                    param($duration,$title,$body,$kind)
                    @{kind='notice';title=$title;body=$body} | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:IL_TEST_CALLS -Encoding UTF8
                }
                $item | Add-Member ScriptMethod Dispose {}
                return $item
            }
            . $Helper -Plan $Plan -Ready $Ready
            """, new UTF8Encoding(true));
        var start = new ProcessStartInfo(PowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", wrapper, "-Helper", Path.Combine(AppContext.BaseDirectory, "fixtures", "install-update.ps1"), "-Plan", plan, "-Ready", Path.Combine(_root, "ready") }) start.ArgumentList.Add(arg);
        start.Environment["IL_TEST_CALLS"] = Path.Combine(_root, "calls.jsonl");
        start.Environment["IL_TEST_EXIT"] = exitCode.ToString(); start.Environment["IL_TEST_REJECT"] = rejectUac.ToString();
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(process.ExitCode == 0, await error + await output);
        return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "result.json"))).RootElement.Clone();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
