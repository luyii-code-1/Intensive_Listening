using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using IL.Core.Management;
using IL.Core.Settings;
using IL.Core.Updates;
using Xunit;

namespace IL.Core.Tests;

public sealed class ManagedUpdateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PackageDownloadSendsDeviceCredentialOnlyToOrganizationOrigin(bool sameOrigin)
    {
        var root = Path.Combine(Path.GetTempPath(), "il-package-auth-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var target = new Uri($"http://127.0.0.1:{port}/api/packages/test");
        var origin = sameOrigin ? target : new Uri($"http://127.0.0.1:{(port == 65535 ? port - 1 : port + 1)}/");
        var capture = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            var header = new StringBuilder(); var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0) throw new IOException();
                header.Append((char)buffer[0]); if (header.Length > 8192) throw new IOException();
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nMZ!"), timeout.Token);
            return header.ToString();
        }, timeout.Token);
        try
        {
            using var key = RSA.Create(2048);
            var update = new ManagedUpdate(Guid.NewGuid().ToString(), "cold", "2.0.2", target.AbsoluteUri, Convert.ToHexString(SHA256.HashData("MZ!"u8)), 3, "");
            update = update with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(update.SignedContent), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
            var downloader = new ManagedUpdateDownloader(root, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), sameOrigin ? "" : "invalid-external-pin", origin, "test-device-token");
            var file = await downloader.DownloadAsync(update, timeout.Token);
            Assert.Equal("MZ!", await File.ReadAllTextAsync(file, timeout.Token));
            Assert.Equal(sameOrigin, (await capture).Contains("Authorization: Bearer test-device-token", StringComparison.OrdinalIgnoreCase));
        }
        finally { listener.Stop(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task SignedUpdateRejectsChangedManifestAndChangedInstaller(string scheme)
    {
        var root=Path.Combine(Path.GetTempPath(),"il-update-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"installer.exe");await File.WriteAllBytesAsync(path,[1,2,3]);
            using var key=RSA.Create(2048);
            var update=new ManagedUpdate(Guid.NewGuid().ToString(),"cold","2.0.2",scheme+"://example.edu/setup.exe",Convert.ToHexString(SHA256.HashData([1,2,3])),3,"");
            update=update with{Signature=Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(update.SignedContent),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))};
            var downloader=new ManagedUpdateDownloader(root,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            await downloader.VerifyAsync(path,update,CancellationToken.None);
            Assert.Throws<InvalidOperationException>(()=>downloader.Validate(update with{Mode="ota"}));
            await File.WriteAllBytesAsync(path,[1,2,4]);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>downloader.VerifyAsync(path,update,CancellationToken.None));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void CertificatePinAcceptsTheEnrolledCertificateAndRejectsAnotherCertificate()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=MDN",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
        using var other=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(2));
        using var handler=MdnConfiguration.CreateHttpHandler(Convert.ToHexString(SHA256.HashData(certificate.RawData)));
        using var message=new HttpRequestMessage(HttpMethod.Get,"https://mdn.example.edu/");
        var validate=handler.ServerCertificateCustomValidationCallback!;
        Assert.True(validate(message,certificate,null,SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(message,other,null,SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validate(message,other,null,SslPolicyErrors.None));
    }

    [Fact]
    public void OrganizationOverlayKeepsPersonalCredentialsAndConsentForPolicyRemoval()
    {
        var personal=AppSettings.Defaults() with{CloudApiKey="personal-key",ThemeMode="light",TelemetryEnabled=false};
        var policy=new ManagedPolicy{Settings=new JsonObject{["cloudApiKey"]="organization-key",["themeMode"]="dark"},HideAsrConfiguration=true};
        policy.Validate();var effective=policy.Apply(personal);
        Assert.Equal("organization-key",effective.CloudApiKey);Assert.Equal("dark",effective.ThemeMode);
        Assert.False(effective.TelemetryEnabled);
        var stored=policy.PreservePersonal(effective,personal);
        Assert.Equal("personal-key",stored.CloudApiKey);Assert.Equal("light",stored.ThemeMode);
    }
}
