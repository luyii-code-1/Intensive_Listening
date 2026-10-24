using System.Net;
using IL.Core.Asr;
using Xunit;

namespace IL.Core.Tests;

public sealed class AsrNoSpeechTests
{
    [Theory]
    [InlineData("{\"error\":{\"code\":\"ASR_RESPONSE_HAVE_NO_WORDS\"}}", true)]
    [InlineData("{\"output\":{\"code\":\"ASR_RESPONSE_HAVE_NO_WORDS\"}}", true)]
    [InlineData("{\"code\":\"InvalidParameter\",\"message\":\"No speech detected\"}", true)]
    [InlineData("{\"code\":\"InvalidApiKey\",\"message\":\"No speech detected\"}", false)]
    [InlineData("{\"code\":\"InvalidParameter\",\"message\":\"Unsupported model\"}", false)]
    public async Task OnlyExplicitNoSpeechResponsesCompleteAsEmpty(string body, bool recoverable)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            using var http = new HttpClient(new Handler(body));
            var result = new AsrClient(http).TranscribeToSrtAsync(new("https://fixture.invalid", "/asr", "model", "key"), file);
            if (recoverable) Assert.Empty(await result);
            else await Assert.ThrowsAsync<AsrException>(() => result);
        }
        finally { File.Delete(file); }
    }
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) });
    }
}
