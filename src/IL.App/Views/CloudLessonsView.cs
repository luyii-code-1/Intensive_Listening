using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private bool _loading;
    private sealed record LocalCopy(string Version, string PackageUuid);

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
            _session = new(); await ReloadAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            services.CloudAccount.Changed -= AccountChanged;
            _session?.Cancel();
        };
        IsVisible = services.CloudAccount.Enabled;
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
            var local = copy is null ? null : _services.Student.Lessons.FirstOrDefault(l => l.Id == copy.PackageUuid);
            var ready = local is not null && copy!.Version == item.Version;
            var action = ready ? "打开" : local is not null ? "下载更新" : "下载";
            var detail = ready ? $"已下载 · 版本 {local!.Lesson.Manifest.PackageVersion}" : local is not null ? "云端有新版本" : "云端课程";
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
                    try { ManagedRuntime.Require("playback"); _services.Student.SelectedLesson = local; await _services.Student.CurrentLoad; }
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
        if (_downloading is not null || !_services.CloudAccount.IsSignedIn) return;
        if (!ManagedRuntime.Allows("importIlp")) { caption.Text = "该功能由您的组织管理。"; return; }
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "下载云端课程", $"此次下载需要 {_listing!.DownloadCredits:0.##} 积分；未完成下载的重试沿用原授权。", "下载")) return;
        _downloading = item.Id; _refresh.IsEnabled = button.IsEnabled = false;
        var ring = new FAProgressRing { IsActive = true, IsIndeterminate = false, Minimum = 0, Maximum = 100, Width = 24, Height = 24 };
        glyph.Content = ring; hint.Text = "下载中";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_session?.Token ?? default);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var account = _accountId;
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
            var manifest = await importer.ReadManifestAsync(file, deadline.Token);
            if (_services.Student.CurrentLesson?.Id == manifest.PackageUuid)
                throw new IOException("请先返回播放首页，再更新正在打开的课程。");
            var lesson = await Task.Run(() => importer.ImportFileAsync(file, replaceExisting: true, ct: deadline.Token), deadline.Token);
            _copies[item.Id] = new(item.Version, lesson.Manifest.PackageUuid);
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            await File.WriteAllTextAsync(IndexPath + ".tmp", JsonSerializer.Serialize(_copies), deadline.Token);
            File.Move(IndexPath + ".tmp", IndexPath, true);
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
