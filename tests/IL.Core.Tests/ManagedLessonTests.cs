using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IL.Core.Ilp;
using IL.Core.Management;
using IL.Core.Settings;
using IL.Core.Student;
using Xunit;

namespace IL.Core.Tests;

public sealed class ManagedLessonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-lessons-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _uuid = Guid.NewGuid().ToString("D");
    private ManagedPolicy _policy = new();
    private readonly MdnConfiguration _configuration;
    private readonly List<string> _receipts = [];
    private int _downloads, _refreshes;
    private bool _inUse, _acceptReceipt = true;
    private string Library => Path.Combine(_root, "library");
    public ManagedLessonTests()
    {
        Directory.CreateDirectory(_root);
        _configuration = new() { OrganizationName = "测试组织", EnrollmentToken = "enrollment", ServerUrl = "http://mdn.example:8443/", DisableSsl = true, UpdatePublicKey = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo()) };
    }
    private (ManagedLesson Item, string Path) Course(int version)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ilp");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            IlpManifest original;
            using (var input = zip.GetEntry("manifest.json")!.Open()) { using var memory = new MemoryStream(); input.CopyTo(memory); original = IlpManifest.FromBytes(memory.ToArray()); }
            zip.GetEntry("manifest.json")!.Delete();
            using var output = zip.CreateEntry("manifest.json").Open();
            output.Write((original with { FormatVersion = 3, PackageUuid = _uuid, PackageVersion = version, Title = "组织课程" }).ToBytes());
        }
        var id = Guid.NewGuid().ToString("D");
        var item = new ManagedLesson(id, "组织课程", _uuid, version, new Uri(_configuration.Server, "api/lessons/" + id).AbsoluteUri,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length, "");
        item = Sign(item); return (item, path);
    }
    private ManagedLesson Sign(ManagedLesson item) => item with { Signature = Convert.ToBase64String(_rsa.SignData(Encoding.UTF8.GetBytes(item.SignedContent), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
    private ManagedLessonReceiver Receiver(Func<ManagedLesson, CancellationToken, Task<string>> download) => new(() => _policy, () => _configuration, _ => "device-token",
        (_, state, _, _) => { _receipts.Add(state); return Task.FromResult(_acceptReceipt); }, Library, _root,
        async (item, ct) => { _downloads++; return await download(item, ct); })
    { IsInUse = _ => Task.FromResult(_inUse), LibraryChanged = () => { _refreshes++; return Task.CompletedTask; } };

    [Fact]
    public async Task AutoImportUpdatesHigherVersionPreservesProgressAndReportsOnceAcrossRestart()
    {
        var old = Course(1); await new IlpImporter(Library).ImportFileAsync(old.Path);
        var progressPath = Path.Combine(_root, "lesson_progress.json");
        await new LessonProgressStore(progressPath).SaveAsync(new Dictionary<string, LessonProgress> { [_uuid] = new(_uuid, 1000, DateTimeOffset.UtcNow, new HashSet<string> { "0:2" }) });
        var before = await File.ReadAllBytesAsync(progressPath);
        var next = Course(2); _policy = new() { Lessons = [next.Item], DisabledFeatures = ["importIlp"] };
        using (var receiver = Receiver((_, _) => Task.FromResult(next.Path))) await receiver.RefreshAsync();
        Assert.Equal(2, (await new IlpLibrary(Library).FindByUuidAsync(_uuid))!.Manifest.PackageVersion);
        Assert.Equal(before, await File.ReadAllBytesAsync(progressPath)); Assert.Contains("imported", _receipts);
        using (var restarted = Receiver((_, _) => throw new Exception("must not download again"))) await restarted.RefreshAsync();
        Assert.Equal(1, _downloads); Assert.Equal(1, _refreshes);
    }
    [Fact]
    public async Task ActiveCourseIsDeferredThenImportedAfterItCloses()
    {
        var course = Course(1); _policy = new() { Lessons = [course.Item] }; _inUse = true;
        using var receiver = Receiver((_, _) => Task.FromResult(course.Path));
        await receiver.RefreshAsync(); Assert.Equal(0, _downloads); Assert.Contains("deferred", _receipts);
        _inUse = false; await receiver.RefreshAsync(); Assert.Equal(1, _downloads); Assert.Contains("imported", _receipts);
    }
    [Fact]
    public async Task CompletedOlderTaskDoesNotMaskPendingHigherVersion()
    {
        var old = Course(1); _policy = new() { Lessons = [old.Item] };
        using var receiver = Receiver((_, _) => Task.FromResult(old.Path)); await receiver.RefreshAsync();
        var next = Course(2); _policy = new() { Lessons = [next.Item, old.Item] }; _inUse = true;
        await receiver.RefreshAsync(); Assert.Contains("等待", receiver.Status); Assert.Contains("deferred", _receipts);
    }
    [Fact]
    public async Task RevokeDuringDownloadNeverImportsAndWithdrawKeepsImportedCourse()
    {
        var course = Course(1); _policy = new() { Lessons = [course.Item] };
        using var receiver = Receiver((_, _) => { _policy = new(); return Task.FromResult(course.Path); });
        await receiver.RefreshAsync(); Assert.Null(await new IlpLibrary(Library).FindByUuidAsync(_uuid));
        _policy = new() { Lessons = [course.Item] };
        using var next = Receiver((_, _) => Task.FromResult(course.Path)); await next.RefreshAsync();
        _policy = new(); await next.RefreshAsync(); Assert.NotNull(await new IlpLibrary(Library).FindByUuidAsync(_uuid));
    }
    [Fact]
    public async Task SameOrLowerCourseVersionDoesNotDownloadOrReplace()
    {
        var current = Course(3); await new IlpImporter(Library).ImportFileAsync(current.Path);
        var same = Course(3); var older = Course(2); _policy = new() { Lessons = [same.Item, older.Item] };
        using var receiver = Receiver((_, _) => throw new Exception("must not download")); await receiver.RefreshAsync();
        Assert.Equal(0, _downloads); Assert.Equal(2, _receipts.Count(s => s == "skipped"));
        Assert.Equal(3, (await new IlpLibrary(Library).FindByUuidAsync(_uuid))!.Manifest.PackageVersion);
    }
    [Fact]
    public async Task ReceiptFailureRetriesAfterRestartWithoutImportingAgain()
    {
        var course = Course(1); _policy = new() { Lessons = [course.Item] }; _acceptReceipt = false;
        using (var receiver = Receiver((_, _) => Task.FromResult(course.Path))) await receiver.RefreshAsync();
        _acceptReceipt = true;
        using (var restarted = Receiver((_, _) => throw new Exception("must not download"))) await restarted.RefreshAsync();
        Assert.Equal(1, _downloads); Assert.Equal(2, _receipts.Count(s => s == "imported"));
    }
    [Fact]
    public async Task TamperedSignatureOrManifestDoesNotImport()
    {
        var course = Course(1); _policy = new() { Lessons = [course.Item with { PackageVersion = 2 }] };
        using var receiver = Receiver((_, _) => Task.FromResult(course.Path)); await receiver.RefreshAsync();
        Assert.Equal(0, _downloads); Assert.Contains("failed", _receipts);
        _policy = new() { Lessons = [Sign(course.Item with { PackageVersion = 2 })] }; await receiver.RefreshAsync();
        Assert.Equal(1, _downloads); Assert.Null(await new IlpLibrary(Library).FindByUuidAsync(_uuid));
    }
    private sealed class ResponseHandler(byte[] body) : HttpMessageHandler
    {
        public string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Authorization = request.Headers.Authorization?.ToString(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }); }
    }
    [Fact]
    public async Task DownloaderAuthenticatesAndRejectsModifiedPayloadAndForeignOrigin()
    {
        var course = Course(1); var bytes = await File.ReadAllBytesAsync(course.Path); var handler = new ResponseHandler(bytes);
        var downloader = new ManagedLessonDownloader(Path.Combine(_root, "download"), _configuration, "device-token", handler);
        var path = await downloader.DownloadAsync(course.Item, CancellationToken.None); Assert.Equal("Bearer device-token", handler.Authorization);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Throws<InvalidOperationException>(() => downloader.Validate(Sign(course.Item with { Url = "http://outside.example/api/lessons/" + course.Item.Id })));
        bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.VerifyAsync(path, course.Item, CancellationToken.None));
    }
    [Fact]
    public async Task SharedBlurSettingMigratesDefaultsAndPersistsMinimumAndMaximum()
    {
        var store = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        Assert.Equal(AppSettings.MaxTranscriptBlurIntensity, AppSettings.FromJson(new JsonObject()).TranscriptBlurIntensity);
        foreach (var value in new[] { AppSettings.MinTranscriptBlurIntensity, AppSettings.MaxTranscriptBlurIntensity })
        { await store.SaveAsync(AppSettings.Defaults() with { TranscriptBlurIntensity = value }); Assert.Equal(value, (await store.LoadAsync()).TranscriptBlurIntensity); }
        Assert.Equal(1, AppSettings.FromJson(new JsonObject { ["transcriptBlurIntensity"] = 0 }).TranscriptBlurIntensity);
        Assert.Equal(8, AppSettings.FromJson(new JsonObject { ["transcriptBlurIntensity"] = 999 }).TranscriptBlurIntensity);
    }
    public void Dispose() { _rsa.Dispose(); Directory.Delete(_root, true); }
}
