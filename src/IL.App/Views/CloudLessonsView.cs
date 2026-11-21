using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using IL.App.Services;
using IL.App.Views.Dialogs;
using IL.Core.Cloud;
using IL.Core.Ilp;
using IL.Core.Management;

namespace IL.App.Views;

public sealed class CloudLessonsView : UserControl
{
    private readonly AppServices _services;
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _status = WorkspaceUi.Text("", 12);
    private readonly Button _refresh;
    private CancellationTokenSource? _session;
    private readonly Dictionary<string, LocalCopy> _copies = new();
    private CloudPlayableList? _listing;
    private string? _accountId, _downloading;
    private bool _loading, _renderQueued;
    internal sealed record LocalCopy(string Version, string PackageUuid, string? Fingerprint = null)
    {
        public bool Matches(IlpManifest manifest) =>
            string.Equals(PackageUuid, manifest.PackageUuid, StringComparison.OrdinalIgnoreCase) && Fingerprint == manifest.ContentFingerprint();
    }

    public CloudLessonsView(AppServices services)
    {
        _services = services;
        _refresh = WorkspaceUi.Button("刷新", ReloadAsync);
        Content = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(0, 24, 0, 0),
            Children = { WorkspaceUi.Row(WorkspaceUi.Text("Intensive Listening 云服务", 17, true), _refresh), _status, _rows }
        };
        AttachedToVisualTree += async (_, _) =>
        {
            services.CloudAccount.Changed += AccountChanged;
            services.Student.Lessons.CollectionChanged += LibraryChanged;
            _session = new(); await ReloadAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            services.CloudAccount.Changed -= AccountChanged;
            services.Student.Lessons.CollectionChanged -= LibraryChanged;
            _session?.Cancel();
        };
        IsVisible = services.CloudAccount.Enabled;
    }
    private void LibraryChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderQueued = false;
            if (_session?.IsCancellationRequested == false && _downloading is null) Render();
        });
    }
    private void AccountChanged() => Dispatcher.UIThread.Post(async () =>
    {
        IsVisible = _services.CloudAccount.Enabled;
        if (!_services.CloudAccount.IsSignedIn)
        {
            _session?.Cancel(); _rows.Children.Clear(); _copies.Clear(); _accountId = null;
            _listing = null; _status.Text = "请在设置中登录云服务。";
        }
        else { _session ??= new(); if (_session.IsCancellationRequested) _session = new(); await ReloadAsync(); }
    });
    private string IndexPath => Path.Combine(_services.DataDirectory, "cloud-lessons", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_accountId!))) + ".json");
    private async Task ReloadAsync()
    {
        IsVisible = _services.CloudAccount.Enabled;
        if (_loading || _downloading is not null) return;
        if (!_services.CloudAccount.IsSignedIn) { _status.Text = "请在设置中登录云服务。"; return; }
        _loading = true; _refresh.IsEnabled = false;
        var owner = _session;
        try
        {
            _status.Text = "正在读取云端课程…";
            var listing = await _services.CloudFiles.PlayableAsync(owner?.Token ?? default);
            if (owner?.IsCancellationRequested == true || !_services.CloudAccount.IsSignedIn) return;
            _accountId = _services.CloudFiles.UserId;
            _copies.Clear();
            if (File.Exists(IndexPath))
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, LocalCopy>>(await File.ReadAllTextAsync(IndexPath)) ?? []) _copies[pair.Key] = pair.Value;
            _listing = listing; Render();
        }
        catch (OperationCanceledException) { _status.Text = "读取超时，请重试。"; }
        catch (Exception error) { _status.Text = Message(error); }
        finally { _loading = false; _refresh.IsEnabled = true; }
    }
    private void Render()
    {
        _rows.Children.Clear();
        if (_listing is null) return;
        _status.Text = _listing.Items.Length == 0 ? "暂无可播放的云端课程。" : $"下载或更新每次 {_listing.DownloadCredits:0.##} 积分";
        foreach (var item in _listing.Items)
        {
            var copy = _copies.GetValueOrDefault(item.Id);
            var local = copy is null ? null : _services.Student.Lessons.FirstOrDefault(l => string.Equals(l.Id, copy.PackageUuid, StringComparison.OrdinalIgnoreCase));
            var matches = local is not null && copy!.Matches(local.Lesson.Manifest);
            var ready = matches && copy!.Version == item.Version;
            var action = ready ? "打开" : matches ? "下载更新" : "下载";
            var detail = ready ? $"已下载 · 版本 {local!.Lesson.Manifest.PackageVersion}" : matches ? "云端有新版本"
                : local is null ? "云端课程" : copy!.Fingerprint is null ? "旧下载记录待核对 · 可从最近课程打开本地副本" : "本地内容已更改 · 可从最近课程打开本地副本";
            var glyph = new ContentControl { Content = WorkspaceUi.Icon(ready ? "music_note" : "cloud", 22), Width = 28, VerticalAlignment = VerticalAlignment.Center };
            var caption = WorkspaceUi.Text(detail, 12);
            var info = new StackPanel { Margin = new Thickness(12, 0), Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { WorkspaceUi.Text(item.Title, 14, true), caption } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(glyph); Grid.SetColumn(info, 1); grid.Children.Add(info);
            var hint = WorkspaceUi.Text(action, 12); hint.VerticalAlignment = VerticalAlignment.Center;
            var button = new Button { Content = new ResponsivePair(grid, hint) { Margin = new Thickness(14, 8) }, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = default, MinHeight = 58 };
            button.Click += async (_, _) =>
            {
                if (ready && local is not null)
                {
                    try { ManagedRuntime.Require("playback"); await _services.Student.OpenCloudCopyAsync(local, copy!.Fingerprint!); }
                    catch (Exception error) { WorkspaceToast.Show(this, "无法打开课程", error.Message, true); }
                    return;
                }
                await DownloadAsync(item, button, glyph, caption, hint);
            };
            _rows.Children.Add(button);
        }
    }
    private async Task DownloadAsync(CloudPlayableItem item, Button button, ContentControl glyph, TextBlock caption, TextBlock hint)
    {
        if (_downloading is not null || !_services.CloudAccount.IsSignedIn || _accountId is null) return;
        if (!ManagedRuntime.Allows("importIlp")) { caption.Text = "该功能由您的组织管理。"; return; }
        var account = _accountId;
        var indexPath = IndexPath;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_session?.Token ?? default);
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "下载云端课程", $"此次下载需要 {_listing!.DownloadCredits:0.##} 积分；未完成下载的重试沿用原授权。", "下载")) return;
        if (deadline.IsCancellationRequested || account != _accountId || _downloading is not null) return;
        _downloading = item.Id; _refresh.IsEnabled = button.IsEnabled = false;
        var ring = new FAProgressRing { IsActive = true, IsIndeterminate = false, Minimum = 0, Maximum = 100, Width = 24, Height = 24 };
        glyph.Content = ring; hint.Text = "下载中";
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var progress = new Progress<CloudTransferProgress>(p =>
            {
                if (_downloading != item.Id) return;
                ring.IsIndeterminate = p.Total is null;
                ring.Value = p.Total > 0 ? p.Bytes * 100d / p.Total.Value : 0;
                caption.Text = p.Total > 0 ? $"{ring.Value:0}%" : "正在下载…";
            });
            var file = await _services.CloudFiles.DownloadAsync(item, progress, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (account != _accountId || !_services.CloudAccount.IsSignedIn) return;
            ManagedRuntime.Require("importIlp");
            var importer = new IlpImporter(_services.LibraryDirectory);
            var manifest = await importer.PreviewManifestAsync(file, deadline.Token);
            var existing = await new IlpLibrary(_services.LibraryDirectory).FindByUuidAsync(manifest.PackageUuid, deadline.Token);
            var installedFingerprint = existing?.Manifest.ContentFingerprint();
            if (existing is not null && installedFingerprint != manifest.ContentFingerprint() &&
                _copies.GetValueOrDefault(item.Id)?.Matches(existing.Manifest) != true &&
                !await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "替换本地课程？", $"本地课库已有「{existing.Manifest.Title}」（版本 {existing.Manifest.PackageVersion}）。是否替换为「{manifest.Title}」（版本 {manifest.PackageVersion}）？", "替换课程"))
            {
                Render();
                return;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (string.Equals(_services.Student.CurrentLesson?.Id, manifest.PackageUuid, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请先返回播放首页，再更新正在打开的课程。");
            var lesson = await Task.Run(() => importer.ImportFileAsync(file, replaceExisting: existing is not null, ct: deadline.Token,
                expectedExistingFingerprint: installedFingerprint), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (account != _accountId || !_services.CloudAccount.IsSignedIn) return;
            _copies[item.Id] = new(item.Version, lesson.Manifest.PackageUuid, lesson.Manifest.ContentFingerprint());
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath + ".tmp", JsonSerializer.Serialize(_copies), deadline.Token);
            File.Move(indexPath + ".tmp", indexPath, true);
            await _services.CloudFiles.CompleteDownloadAsync(item, deadline.Token);
            await _services.Student.RefreshAsync(preservePlayback: true);
            Render();
        }
        catch (OperationCanceledException) { caption.Text = "下载超时或已中断"; glyph.Content = WorkspaceUi.Icon("sync"); hint.Text = "重试"; }
        catch (Exception error) { caption.Text = Message(error); glyph.Content = WorkspaceUi.Icon("sync"); hint.Text = "重试"; }
        finally { _downloading = null; button.IsEnabled = _refresh.IsEnabled = true; }
    }
    private string Message(Exception error)
    {
        if (error is CloudAuthenticationException auth) _services.CloudAccount.AuthenticationFailed(auth.Message);
        return error is CloudApiException or CloudAuthenticationException or IOException ? error.Message : "连接失败，请重试。";
    }
}
