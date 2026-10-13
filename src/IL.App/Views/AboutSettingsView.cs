using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using IL.App.Services;
using IL.App.Views.Dialogs;
using IL.Core.Ilp;

namespace IL.App.Views;

internal sealed class AboutSettingsView : StackPanel
{
    private const string Repository = "https://github.com/luyii-code-1/Intensive_Listening";
    public AboutSettingsView(Action showUpdates)
    {
        MaxWidth = 1040; Spacing = 12;
        var identity = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 20, Margin = new Thickness(16, 12, 16, 16) };
        identity.Children.Add(new Image { Source = AppBrand.Image, Width = 88, Height = 88, VerticalAlignment = VerticalAlignment.Center });
        var name = WorkspaceUi.Stack(WorkspaceUi.Text("Intensive Listening", 26, true), WorkspaceUi.Text(AppBuildInfo.ReleaseName, 16), WorkspaceUi.Text($"版本 {AppBuildInfo.Version} · 构建 {AppBuildInfo.Commit}", 12));
        Grid.SetColumn(name, 1); identity.Children.Add(name); Children.Add(identity);

        Children.Add(Section("Intensive Listening", "music_note",
            WorkspaceUi.Text("Copyright © 2026 Luyii", 14, true),
            WorkspaceUi.Text("本项目依据 GNU General Public License v3.0（GPL-3.0-only）开放源代码。"),
            Info("作者", "Luyii", Link("作者主页", "https://github.com/luyii-code-1")),
            Info("项目仓库", "luyii-code-1 / Intensive_Listening", Link("GitHub", Repository)),
            Info("发布版本", AppBuildInfo.Display, WorkspaceUi.Button("查看更新", () => { showUpdates(); return Task.CompletedTask; })),
            Info("ILP 数据格式", IlpManifest.SupportedVersion.ToString()),
            WorkspaceUi.Row(Link("项目主页", "https://il.luyii.cn/"), Link("使用文档", "https://il.luyii.cn/guide.html"), Link("发布记录", Repository + "/releases")),
            WorkspaceUi.Row(Document("开源许可证", "GPL-3.0.txt"), Document("用户协议", "eula_zh_cn.txt"), Document("隐私说明", "privacy_zh_cn.txt"))));

        Children.Add(Section("问题反馈", "info",
            WorkspaceUi.Text("报告问题时可附上版本、构建与运行环境信息。"),
            WorkspaceUi.Row(Link("问题与功能建议", Repository + "/issues"), Link("贡献者", Repository + "/graphs/contributors"), WorkspaceUi.Button("复制诊断信息", CopyDiagnosticsAsync))));

        Children.Add(Section("鸣谢与借鉴", "education",
            WorkspaceUi.Text("部分设计和代码借鉴了 ClassIsland 2（Ci2），包括欢迎引导、应用内通知及设置页面的组织方式。", 14, true),
            WorkspaceUi.Text("感谢 HelloWRC 及 ClassIsland 贡献者。ClassIsland 依据 GNU GPL v3.0 开放源代码。"),
            WorkspaceUi.Row(Link("ClassIsland 仓库", "https://github.com/ClassIsland/ClassIsland"), Link("Ci2 关于页源码", "https://github.com/ClassIsland/ClassIsland/blob/master/ClassIsland/Views/SettingPages/AboutSettingsPage.axaml"), Document("借鉴与版权声明", "ClassIsland-NOTICE.txt")),
            WorkspaceUi.Text("界面使用 Avalonia 与 FluentAvalonia，应用字体使用 Huawei HarmonyOS Sans SC。Copyright 2021 Huawei Device Co., Ltd."),
            WorkspaceUi.Row(Document("第三方组件声明", "THIRD_PARTY_NOTICES.md"), Document("组件版本与来源", "csharp-third-party.txt"), Document("字体许可", "HarmonyOS-Sans-LICENSE.txt"), Document("图标许可", "fluent_ui_license.txt"))));

        Children.Add(Section("运行信息", "document",
            Info("操作系统", RuntimeInformation.OSDescription), Info("体系架构", RuntimeInformation.ProcessArchitecture.ToString()), Info("运行时", RuntimeInformation.FrameworkDescription)));
    }
    private static Expander Section(string title, string icon, params Control[] contents)
    {
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(18, 12) };
        foreach (var content in contents) body.Children.Add(content);
        return new Expander { Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { WorkspaceUi.Icon(icon, 20), WorkspaceUi.Text(title, 16, true) } }, Content = body, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    }
    private static Grid Info(string title, string value, Control? action = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(WorkspaceUi.Stack(WorkspaceUi.Text(title, 12, true), new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap }));
        if (action is not null) { action.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(action, 1); row.Children.Add(action); }
        return row;
    }
    private static Button Link(string title, string url)
    {
        var button = WorkspaceUi.Button(title, () => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; });
        button.Classes.Add("subtle"); return button;
    }
    private Button Document(string title, string filename) => WorkspaceUi.Button(title, async () =>
        await AppDialogs.DocumentAsync(WorkspaceUi.Owner(this), title, (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "assets", "legal", filename))).TrimEnd('\0')));
    private async Task CopyDiagnosticsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用。");
        await clipboard.SetTextAsync($"Intensive Listening {AppBuildInfo.Display}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.ProcessArchitecture}\n{RuntimeInformation.FrameworkDescription}\nILP 格式 {IlpManifest.SupportedVersion}");
        WorkspaceToast.Show(this, "诊断信息已复制");
    }
}
