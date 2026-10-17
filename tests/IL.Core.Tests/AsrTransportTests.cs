using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IL.Core.Asr;
using Xunit;

namespace IL.Core.Tests;

public sealed class AsrTransportTests
{
    [Fact]
    public async Task ProductionTransportSendsCompleteLengthDelimitedAudioAndReportsProgress()
    {
        var file = Path.Combine(Path.GetTempPath(), "il-asr-transport-" + Guid.NewGuid().ToString("N") + ".wav");
        var audio = new byte[150000]; new Random(4).NextBytes(audio); await File.WriteAllBytesAsync(file, audio);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = connection.GetStream(); var header = new StringBuilder(); var next = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { Assert.Equal(1, await stream.ReadAsync(next, deadline.Token)); header.Append((char)next[0]); }
            var headers = header.ToString().Split("\r\n").Skip(1).Where(line => line.Contains(':'))
                .Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal("Bearer fixture-key", headers["Authorization"]);
            var payload = new byte[int.Parse(headers["Content-Length"])]; await stream.ReadExactlyAsync(payload, deadline.Token);
            using var json = JsonDocument.Parse(payload);
            var encoded = json.RootElement.GetProperty("input").GetProperty("messages")[0].GetProperty("content")[0].GetProperty("audio").GetString()!;
            Assert.Equal(audio, Convert.FromBase64String(encoded.Split(',')[1]));
            const string body = "{\"code\":\"ASR_RESPONSE_HAVE_NO_WORDS\"}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 400 Bad Request\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), deadline.Token);
        });
        try
        {
            var values = new List<AsrProgress>();
            var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var result = await new AsrClient().TranscribeToSrtAsync(new(endpoint, "/asr", "fixture-model", "fixture-key"), file, new InlineProgress(values.Add), deadline.Token);
            Assert.Empty(result); await receiving;
            Assert.Contains(values, value => value.Stage == AsrStage.Uploading && value.StageFraction is > 0 and < 1);
            Assert.Contains(values, value => value.BytesDone == audio.Length);
        }
        finally { listener.Stop(); File.Delete(file); }
    }
    private sealed class InlineProgress(Action<AsrProgress> report) : IProgress<AsrProgress>
    { public void Report(AsrProgress value) => report(value); }
}
