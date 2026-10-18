using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using IL.App.Services;
using IL.App.Views.Dialogs;
using IL.Core.Cloud;
using IL.Core.Ilp;
using IL.Core.Projects;

namespace IL.App.Views;

public sealed class CloudAuthoringView : UserControl
{
    private readonly CloudAccountService _account;
    private readonly AppServices _services;
    private readonly Func<Task> _saveDraft, _imported;
    private readonly TextBlock _status = WorkspaceUi.Text("未登录 Intensive Listening 云服务", 13);
    private readonly Button _login, _cancel, _logout;
    private readonly StackPanel _resources = new() { Spacing = 14 };
    private readonly TextBlock _identity = WorkspaceUi.Text("", 13), _quota = WorkspaceUi.Text("", 12), _transferStatus = WorkspaceUi.Text("", 12);
    private decimal? _downloadCredits;
    private readonly TextBlock _price = WorkspaceUi.Text("", 12);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
    private readonly ListBox _files = List(), _courses = List(), _jobs = List();
    private readonly ComboBox _localProject = new() { MinWidth = 180, MaxWidth = 420, PlaceholderText = "选择本地工程" };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Button _reload, _uploadFile, _uploadProject, _cancelTransfer;
    private CancellationTokenSource? _transferCancellation;
    private bool _restored, _hadCredentials, _loading, _transferring;

    public CloudAuthoringView(AppServices services, Func<Task> saveDraft, Func<Task> imported)
    {
        _services = services; _account = services.CloudAccount; _saveDraft = saveDraft; _imported = imported;
        _login = WorkspaceUi.Button("浏览器登录", () => _account.LoginAsync(), true);
        _cancel = WorkspaceUi.Button("取消登录", () => { _account.CancelLogin(); return Task.CompletedTask; });
        _logout = WorkspaceUi.Button("退出登录", _account.SignOutAsync);
        _reload = WorkspaceUi.Button("刷新", ReloadResourcesAsync);
        _uploadFile = WorkspaceUi.Button("上传 ILP / 工程 ZIP", UploadFileAsync);
        _uploadProject = WorkspaceUi.Button("上传所选工程", UploadProjectAsync);
        _cancelTransfer = WorkspaceUi.Button("取消传输", () => { _transferCancellation?.Cancel(); return Task.CompletedTask; });
        _localProject.ItemTemplate = new FuncDataTemplate<CourseProject>((project, _) => WorkspaceUi.Text(project?.Title ?? ""));
        _files.ItemTemplate = new FuncDataTemplate<CloudFile>((file, _) => file is null ? null : ResourceRow(file.Title,
            $"{(file.Kind == "project" ? "项目文件" : "ILP 精听包")} · {Size(file.Bytes)}", WorkspaceUi.Button("下载并导入", () => DownloadAsync(file.Id, file.Title, "file", file.Kind))));
        _courses.ItemTemplate = new FuncDataTemplate<CloudCourse>((course, _) => course is null ? null : ResourceRow(course.Title, $"云制作工程 · 修订 {course.Revision}",
            WorkspaceUi.Row(WorkspaceUi.Button("下载工程", () => DownloadAsync(course.Id, course.Title, "course", "project")))));
        _jobs.ItemTemplate = new FuncDataTemplate<CloudJob>((job, _) => job is null ? null : WorkspaceUi.Stack(
            WorkspaceUi.Text($"{JobKind(job.Kind)} · {JobState(job.State)}", 13, true),
            WorkspaceUi.Text(string.Join(" · ", new[] { job.Step, job.Error, job.CreditNotice }.Where(value => !string.IsNullOrWhiteSpace(value))), 12)));
        _resources.Children.Add(WorkspaceUi.Row(_identity, _reload));
        _resources.Children.Add(_quota);
        _resources.Children.Add(_price);
        _resources.Children.Add(WorkspaceUi.Row(_uploadFile, _localProject, _uploadProject));
        _resources.Children.Add(WorkspaceUi.Row(_transferStatus, _cancelTransfer));
        _resources.Children.Add(_progress);
        _resources.Children.Add(WorkspaceUi.Text("工程文件", 15, true)); _resources.Children.Add(_files);
        _resources.Children.Add(WorkspaceUi.Text("云制作工程", 15, true)); _resources.Children.Add(_courses);
        _resources.Children.Add(new Expander { Header = "制作任务", Content = _jobs, HorizontalAlignment = HorizontalAlignment.Stretch });
        _poll.Tick += async (_, _) => await ReloadResourcesAsync();
        var web = WorkspaceUi.Button("打开 Intensive Listening 云服务", () =>
        {
            Process.Start(new ProcessStartInfo(CloudAuthClient.Server.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _login, _cancel, _logout, web }) { button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button); }
        Content = new StackPanel
        {
            MaxWidth = 1040, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { WorkspaceUi.Text("云端制作与传输", 20, true), WorkspaceUi.Text("连接云端账号，管理云端制作任务和精听包。", 12) } },
                _status, actions, _resources
            }
        };
        AttachedToVisualTree += async (_, _) =>
        {
            _account.Changed += Changed;
            Refresh();
            if (!_restored) { _restored = true; await _account.RestoreAsync(); }
            else await ReloadResourcesAsync();
        };
        DetachedFromVisualTree += (_, _) => { _account.Changed -= Changed; _poll.Stop(); _transferCancellation?.Cancel(); };
        Refresh();
    }

    private void Changed() => Dispatcher.UIThread.Post(Refresh);
    private void Refresh()
    {
        _status.Text = _account.Status;
        _login.Content = _account.IsSignedIn ? "重新授权" : "浏览器登录";
        _login.IsEnabled = _account.Enabled && !_account.IsBusy && !_transferring && !_loading;
        _logout.IsVisible = _account.IsSignedIn;
        _logout.IsEnabled = !_account.IsBusy && !_transferring && !_loading;
        _cancel.IsVisible = _account.IsBusy;
        _resources.IsVisible = _account.IsSignedIn && !_account.IsBusy;
        _reload.IsEnabled = _uploadFile.IsEnabled = _uploadProject.IsEnabled = !_account.IsBusy && !_loading && !_transferring;
        _cancelTransfer.IsVisible = _transferring;
        _files.IsEnabled = _courses.IsEnabled = !_transferring && !_loading && !_account.IsBusy;
        if (!_account.IsSignedIn)
        {
            _poll.Stop(); _identity.Text = _quota.Text = "";
            _downloadCredits = null; _price.Text = "";
            _files.ItemsSource = null; _courses.ItemsSource = null; _jobs.ItemsSource = null;
        }
        var newlySignedIn = _account.IsSignedIn && !_hadCredentials && !_account.IsBusy;
        _hadCredentials = _account.IsSignedIn && !_account.IsBusy;
        if (newlySignedIn) _ = ReloadResourcesAsync();
    }

    private async Task ReloadResourcesAsync()
    {
        if (_loading || _transferring || !_account.IsSignedIn || _account.IsBusy) return;
        _loading = true; Refresh();
        try
        {
            var snapshot = await _services.CloudFiles.SnapshotAsync();
            if (!_account.IsSignedIn) return;
            _identity.Text = string.IsNullOrWhiteSpace(snapshot.User.Name) ? snapshot.User.Email : $"{snapshot.User.Name} · {snapshot.User.Email}";
            _quota.Text = $"空间 {Size(snapshot.Storage.UsedBytes)} / {Size(snapshot.Storage.LimitBytes)} · 制作席位 {snapshot.Storage.UsedSlots} / {snapshot.Storage.Slots}\n可用积分 {snapshot.Wallet.Available:0.##} · 冻结 {snapshot.Wallet.Held:0.##}";
            _downloadCredits = snapshot.Wallet.Prices?.Delivery;
            _price.Text = _downloadCredits is { } credits ? $"上传免费 · 下载每次 {credits:0.##} 积分" : "暂时无法读取下载价格，请刷新。";
            _files.ItemsSource = snapshot.Files.Where(file => file.Kind == "project").ToArray();
            _courses.ItemsSource = snapshot.Courses;
            _jobs.ItemsSource = snapshot.Jobs;
            var selected = (_localProject.SelectedItem as CourseProject)?.Id;
            var projects = await _services.Projects.LoadAllAsync();
            _localProject.ItemsSource = projects;
            _localProject.SelectedItem = projects.FirstOrDefault(project => project.Id == selected);
            if (TopLevel.GetTopLevel(this) is not null && snapshot.Jobs.Any(job => job.State is "queued" or "running")) _poll.Start();
            else _poll.Stop();
        }
        catch (Exception error) { PresentError(error); _poll.Stop(); }
        finally { _loading = false; Refresh(); }
    }

    private async Task UploadFileAsync()
    {
        var path = await WorkspaceUi.Pick(this, "上传 ILP 或工程 ZIP", "ilp", "zip");
        if (path is null) return;
        await TransferAsync(async ct =>
        {
            await _services.CloudFiles.UploadAsync(path, Progress(), ct);
            _transferStatus.Text = "上传完成";
        });
    }

    private async Task UploadProjectAsync()
    {
        if (_localProject.SelectedItem is not CourseProject selected) { _transferStatus.Text = "请先选择本地工程。"; return; }
        await TransferAsync(async ct =>
        {
            await _saveDraft();
            var project = await _services.Projects.LoadByIdAsync(selected.Id, ct) ?? throw new IOException("本地工程已不存在。");
            var cache = Path.Combine(_services.DataDirectory, "cloud-transfers", "project-uploads");
            Directory.CreateDirectory(cache);
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(project.Id + ":" + project.UpdatedAt.ToString("O"))));
            var archive = Path.Combine(cache, identity + ".zip");
            if (!File.Exists(archive))
            {
                try
                {
                    await Task.Run(() => _services.Projects.ExportZipToFileAsync(project, archive + ".tmp", ct), ct);
                    File.Move(archive + ".tmp", archive, true);
                }
                finally { File.Delete(archive + ".tmp"); }
            }
            await _services.CloudFiles.UploadAsync(archive, Progress(), ct);
            File.Delete(archive);
            _transferStatus.Text = "工程上传完成";
        });
    }

    private async Task DownloadAsync(string id, string title, string kind, string format)
    {
        var extension = format == "ilp" ? "ilp" : "zip";
        var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "云端课程";
        var output = await WorkspaceUi.Save(this, "保存云端文件", name + "." + extension, extension);
        if (output is null) return;
        if (_downloadCredits is null) { _transferStatus.Text = "请先刷新并读取下载价格。"; return; }
        if (!await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "下载并导入", $"此次下载授权需要 {_downloadCredits:0.##} 积分。未完成下载的重试沿用原授权。", "下载并导入")) return;
        await TransferAsync(async ct =>
        {
            var file = await _services.CloudFiles.DownloadAsync(id, kind, format, Progress(), ct);
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var source = File.OpenRead(file))
                await using (var target = File.Create(temporary)) await source.CopyToAsync(target, ct);
                File.Move(temporary, output, true);
            }
            finally { File.Delete(temporary); }
            if (format == "ilp")
            {
                var importer = new IlpImporter(_services.LibraryDirectory);
                var manifest = await importer.ReadManifestAsync(file, ct);
                var existing = await new IlpLibrary(_services.LibraryDirectory).FindByUuidAsync(manifest.PackageUuid, ct);
                if (existing is null || await AppDialogs.ConfirmAsync(WorkspaceUi.Owner(this), "替换本地精听包？", "课库中已有相同课程。文件已保存，是否替换课库中的版本？", "替换"))
                    await Task.Run(() => importer.ImportFileAsync(file, existing is not null, ct: ct), ct);
            }
            else await Task.Run(() => _services.Projects.ImportZipAsync(file, ct), ct);
            await _services.CloudFiles.CompleteDownloadAsync(id, kind, format, ct);
            await _imported();
            _transferStatus.Text = "下载完成，文件已保存并处理导入。";
        });
    }

    private async Task TransferAsync(Func<CancellationToken, Task> action)
    {
        if (_transferring || _loading || _account.IsBusy || !_account.IsSignedIn) return;
        _transferring = true; _poll.Stop();
        using var cancellation = new CancellationTokenSource(); _transferCancellation = cancellation;
        _transferStatus.Text = "正在准备传输…"; _progress.Value = 0; _progress.IsVisible = true; Refresh();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { _transferStatus.Text = "传输已取消，可重试本次操作。"; }
        catch (Exception error) { PresentError(error); }
        finally { _transferCancellation = null; _transferring = false; _progress.IsVisible = false; Refresh(); }
        await ReloadResourcesAsync();
    }

    private IProgress<CloudTransferProgress> Progress()
    {
        var owner = _transferCancellation;
        return new ThrottledProgress(progress => Dispatcher.UIThread.Post(() =>
        {
            if (!_transferring || !ReferenceEquals(owner, _transferCancellation)) return;
            _progress.IsIndeterminate = progress.Total is null;
            if (progress.Total > 0) _progress.Value = progress.Bytes * 100d / progress.Total.Value;
            _transferStatus.Text = progress.Total.HasValue ? $"{Size(progress.Bytes)} / {Size(progress.Total.Value)}" : Size(progress.Bytes);
        }));
    }
    private void PresentError(Exception error)
    {
        if (error is CloudAuthenticationException auth) _account.AuthenticationFailed(auth.Message);
        _transferStatus.Text = error is CloudApiException or CloudAuthenticationException ? error.Message : "云服务操作未完成，请检查网络或本地文件权限后重试。";
    }
    private static ListBox List()
    {
        var list = new ListBox { MaxHeight = 360, MinHeight = 48 };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        return list;
    }
    private static Control ResourceRow(string title, string detail, Control action) => WorkspaceUi.SettingsRow("cloud", title, detail, action, compact: true);
    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : $"{bytes / 1024d:0.0} KiB";
    private static string JobState(string state) => state switch { "queued" => "等待中", "running" => "制作中", "succeeded" => "已完成", "failed" => "失败", "cancelled" => "已取消", _ => state };
    private static string JobKind(string kind) => kind switch { "create" => "制作", "optimize" => "优化", _ => kind };
    private sealed class ThrottledProgress(Action<CloudTransferProgress> report) : IProgress<CloudTransferProgress>
    {
        private long _last;
        public void Report(CloudTransferProgress value)
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _last) < 150 && value.Bytes != value.Total) return;
            Interlocked.Exchange(ref _last, now); report(value);
        }
    }
}
