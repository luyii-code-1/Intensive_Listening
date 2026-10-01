using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using IL.Core.Infrastructure;
using IL.Core.Settings;
using IL.Core.Telemetry;

namespace IL.App.Views.Dialogs;

/// <summary>Runs in a fresh process after a fatal exit, independently of the failed dispatcher.</summary>
public sealed class CrashReportWindow : Window
{
    public CrashReportWindow(string path, bool submit = true)
    {
        var report = CrashReportStore.Load(path) ?? new CrashReport { Message = "崩溃报告读取失败，请查看日志目录。" };
        var root = Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName;
        var store = new CrashReportStore(root);
        Icon = AppBrand.WindowIcon();
        Title = "Intensive Listening · 崩溃报告"; Width = 720; Height = 540; MinWidth = 540; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var status = WorkspaceUi.Text(report.UploadAllowed ? "报告已保存在本机，正在尝试提交遥测。" : "报告已保存在本机。遥测已关闭。", 13);
        var details = new SelectableTextBlock { Text = report.Summary, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var buttons = WorkspaceUi.Row(
            WorkspaceUi.Button("复制报告", async () => { if (Clipboard != null) await Clipboard.SetTextAsync(report.Summary); }),
            WorkspaceUi.Button("打开报告目录", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(store.DirectoryPath) { UseShellExecute = true }); return Task.CompletedTask; }),
            WorkspaceUi.Button("关闭", () => { Close(); return Task.CompletedTask; }, true));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var body = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        body.Children.Add(WorkspaceUi.Text(report.Fatal ? "程序意外退出" : "后台任务意外退出", 23, true));
        var message = WorkspaceUi.Text(report.Message, 14); message.Margin = new Thickness(0, 10, 0, 12); Grid.SetRow(message, 1); body.Children.Add(message);
        status.Margin = new Thickness(0, 0, 0, 14); Grid.SetRow(status, 2); body.Children.Add(status);
        var scroll = new ScrollViewer { Content = details, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); body.Children.Add(scroll); buttons.Margin = new Thickness(0, 18, 0, 0); Grid.SetRow(buttons, 4); body.Children.Add(buttons);
        SpringMotion.Entrance(body, 8, .99); Content = body;
        AppTelemetry? telemetry = null;
        Opened += async (_, _) =>
        {
            store.Save((CrashReportStore.Load(path) ?? report) with { Displayed = true });
            if (!submit) { status.Text = (CrashReportStore.Load(path)?.AcceptedByTelemetryAt != null) ? "报告已提交至遥测组件。" : report.UploadAllowed ? "报告已保存在本机；未送达的报告将在下次启动重试。" : "报告已保存在本机。遥测已关闭。"; return; }
            var settings = await new AppSettingsStore(Path.Combine(root, "settings.json")).LoadAsync();
            telemetry = new(dataDirectory: root);
            try
            {
                await telemetry.ApplyConsentAsync(settings.TelemetryEnabled && report.UploadAllowed, true);
                await store.FlushAsync(settings.TelemetryEnabled, async item =>
                {
                    var latest = await new AppSettingsStore(Path.Combine(root, "settings.json")).LoadAsync();
                    return latest.TelemetryEnabled && await telemetry.ReportCrashAsync(item);
                });
                var latestReport = CrashReportStore.Load(path);
                status.Text = latestReport?.AcceptedByTelemetryAt != null ? "报告已提交至遥测组件。" : settings.TelemetryEnabled && report.UploadAllowed ? "报告已保存在本机；未送达的报告将在下次启动重试。" : "报告已保存在本机。遥测已关闭。";
            }
            catch (Exception e) { AppLog.Warning("崩溃报告提交失败，将在下次启动重试", e); status.Text = "报告已保存在本机，将在下次启动重试。"; }
            finally { await telemetry.ShutdownAsync(); telemetry = null; }
        };
    }
}
