using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Platform;
using IL.App.Services;
using IL.Core.Mcp;
using IL.Core.Projects;
using IL.Core.Transcription;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task CheckResidenceAsync(string isolated)
    {
        var checks = new List<string>();
        if (!AssetLoader.Exists(new Uri("avares://IL.App/Assets/app_icon.ico"))) throw new InvalidOperationException("Tray icon resource is missing.");
        checks.Add("embedded tray icon");
        await using var server = new AppPrivateApiServer((_, _) => Task.FromResult<object?>(new { ready = true }), Path.Combine(isolated, "mcp", "discovery.json"), 0);
        await server.StartAsync();
        var port = server.Port;
        var discovery = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(isolated, "mcp", "discovery.json")))!;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcript = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new TranscriptionQueue(async (_, _, token, _) => { started.TrySetResult(); return await transcript.Task.WaitAsync(token); },
            _ => Task.FromResult<DuplicateMatch?>(null), new QueueStore(Path.Combine(isolated, "queue.json")), new SrtRecognitionCache(Path.Combine(isolated, "cache")), () => "residence-fixture");
        var projects = new CourseProjectStore(Path.Combine(isolated, "projects"));
        await using var binding = new ProjectTranscriptionBinding(projects, queue);
        var audio = Path.Combine(isolated, "audio.wav"); await File.WriteAllTextAsync(audio, "residence fixture");
        var pending = await binding.EnqueueAsync(await projects.CreateAsync(audio));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saves = 0; var stops = 0; var shutdowns = 0; var closed = false;
        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window { Width = 800, Height = 600 };
        window.Closed += (_, _) => closed = true;
        using var residence = new DesktopResidence(window, () => { saves++; return Task.CompletedTask; }, async () =>
        { stops++; await stopGate.Task; await queue.DisposeAsync(); await binding.DisposeAsync(); await server.StopAsync(); }, () => shutdowns++, createTray: false);
        window.Show(); window.Close();
        if (window.IsVisible || closed || saves != 1 || stops != 0 || !queue.IsBusy) throw new InvalidOperationException("Closing did not preserve resident work.");
        checks.Add("close hides window and preserves active transcription");
        foreach (var method in new[] { "initialize", "tools/list" })
        {
            using var response = await client.PostAsJsonAsync(discovery["mcpUrl"]!.ToString(), new { jsonrpc = "2.0", id = 1, method, @params = new { } });
            response.EnsureSuccessStatusCode();
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            if (reply["result"] == null || reply["error"] != null) throw new InvalidOperationException("Resident MCP did not respond to " + method);
        }
        if (server.Port != port) throw new InvalidOperationException("Hiding changed the MCP port.");
        checks.Add("real MCP initialize and tools/list respond while hidden on the same port");
        const string srt = "1\n00:00:00,000 --> 00:00:01,000\nCompleted while hidden.\n";
        transcript.SetResult(srt); await queue.WaitForIdleAsync(); await binding.FlushAsync();
        var saved = await projects.LoadByIdAsync(pending.Id);
        if (saved?.Transcript != srt || saved.Step != CourseProjectStep.Review) throw new InvalidOperationException("Resident transcription was not bound to its project.");
        checks.Add("hidden transcription completes and persists SRT and Review step");
        residence.Restore();
        if (!window.IsVisible || closed) throw new InvalidOperationException("Resident window did not reopen.");
        window.WindowState = WindowState.Minimized; window.Close(); residence.Restore();
        if (!window.IsVisible || window.WindowState == WindowState.Minimized) throw new InvalidOperationException("Minimized resident window did not restore.");
        checks.Add("restore reuses visible window and restores minimized state");
        var exit = residence.ExitAsync(); var repeated = residence.ExitAsync();
        if (stops != 1 || !ReferenceEquals(exit, repeated)) throw new InvalidOperationException("Repeated exit started duplicate shutdown.");
        stopGate.SetResult(); await exit;
        if (!closed || server.IsRunning || shutdowns != 1 || stops != 1) throw new InvalidOperationException("Explicit exit did not stop services exactly once.");
        checks.Add("explicit exit stops services and closes once");
        await File.WriteAllTextAsync(Path.Combine(_output, "residence.json"), JsonSerializer.Serialize(new { success = true, checks, Boundary = "Headless in-memory window lifecycle and real localhost MCP HTTP; native tray interaction acceptance belongs to the user." }, Json));
    }
}
