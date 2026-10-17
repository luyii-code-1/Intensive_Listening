using System.Net;
using System.Net.Http.Headers;

namespace IL.Core.Cloud;

public sealed class CloudApiTransport : IDisposable
{
    private readonly CloudAuthClient _authentication;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public CloudApiTransport(CloudAuthClient authentication, HttpClient? http = null)
    {
        _authentication = authentication;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        var target = request.RequestUri;
        if (target is null || !target.IsAbsoluteUri || target.Scheme != CloudAuthClient.Server.Scheme
            || target.Host != CloudAuthClient.Server.Host || target.Port != CloudAuthClient.Server.Port || target.UserInfo.Length != 0)
            throw new ArgumentException("IL Cloud 凭据只能用于云服务自身的接口请求。", nameof(request));
        var access = await _authentication.GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await _authentication.InvalidateAsync(access, CancellationToken.None);
            throw new CloudAuthenticationException("login_required", "登录已失效，请重新授权 IL Cloud。");
        }
        return response;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
