using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using IL.Core.Infrastructure;

namespace IL.App.Services;

/// <summary>Hiding the workspace preserves services; explicit exit releases them once.</summary>
public sealed class DesktopResidence : IDisposable
{
    private readonly Window _window;
    private readonly Func<Task> _save, _stop;
    private readonly Action _shutdown;
    private readonly TrayIcon? _tray;
    private Task? _exit;
    private bool _exiting;
    private WindowState _visibleState = WindowState.Normal;
    public DesktopResidence(Window window, Func<Task> save, Func<Task> stop, Action shutdown, bool createTray = true)
    {
        _window = window; _save = save; _stop = stop; _shutdown = shutdown;
        if (createTray)
        {
            using var icon = AssetLoader.Open(new Uri("avares://IL.App/Assets/app_icon.ico"));
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开主窗口"); open.Click += (_, _) => Restore();
            var exit = new NativeMenuItem("退出"); exit.Click += async (_, _) => await ExitAsync();
            menu.Items.Add(open); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
            _tray = new TrayIcon { Icon = new WindowIcon(icon), Menu = menu, ToolTipText = "Intensive Listening", IsVisible = true };
            _tray.Clicked += (_, _) => Restore();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
            window.Icon = _tray.Icon;
        }
        window.Closing += Closing;
    }
    public void UpdateMcpState(bool running)
    { if (_tray != null) _tray.ToolTipText = running ? "Intensive Listening · MCP 服务运行中" : "Intensive Listening · MCP 已关闭"; }
    public void Restore()
    {
        if (_exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _visibleState;
        _window.Activate();
        foreach (var owned in _window.OwnedWindows.Where(w => w.IsVisible)) owned.Activate();
    }
    private void Closing(object? sender, WindowClosingEventArgs args)
    {
        if (_exiting) return;
        if (args.CloseReason == WindowCloseReason.OSShutdown) { _ = ExitAsync(closeWindow: false); return; }
        args.Cancel = true;
        if (args.CloseReason == WindowCloseReason.ApplicationShutdown) { _ = ExitAsync(); return; }
        if (_window.WindowState != WindowState.Minimized) _visibleState = _window.WindowState;
        _window.Hide();
        _ = SaveAsync();
    }
    private async Task SaveAsync()
    { try { await _save(); } catch (Exception error) { AppLog.Warning("后台保存工程失败", error); } }
    public Task ExitAsync(bool closeWindow = true) => _exiting ? _exit ?? Task.CompletedTask : _exit = StopAsync(closeWindow);
    private async Task StopAsync(bool closeWindow)
    {
        _exiting = true;
        try { await SaveAsync(); await _stop(); }
        catch (Exception error) { AppLog.Warning("退出时保存服务状态失败", error); }
        finally
        {
            Dispose();
            if (closeWindow) { _window.Close(); _shutdown(); }
        }
    }
    public void Dispose() { _window.Closing -= Closing; _tray?.Dispose(); }
}
