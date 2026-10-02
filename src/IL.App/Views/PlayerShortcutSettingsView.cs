using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using IL.App.Services;
using IL.App.Views.Dialogs;
using IL.Core.Settings;

namespace IL.App.Views;

internal sealed class PlayerShortcutSettingsView : StackPanel
{
    private readonly Func<IReadOnlyDictionary<PlayerShortcutAction, string>, Task> _save;
    private readonly Dictionary<PlayerShortcutAction, Button> _buttons = new();
    private readonly TextBlock _status = WorkspaceUi.Text("", 12);
    private Dictionary<PlayerShortcutAction, string> _bindings = new();
    private PlayerShortcutAction? _recording;
    private readonly HashSet<Key> _capturedKeys = new();

    public PlayerShortcutSettingsView(Func<IReadOnlyDictionary<PlayerShortcutAction, string>, Task> save)
    {
        _save = save; Spacing = 8; Margin = new Thickness(18, 8, 18, 10);
        Children.Add(WorkspaceUi.Text("点击按键按钮，再按翻页笔或键盘上的单键；Esc 取消录入。", 12));
        foreach (var choice in PlayerShortcuts.Actions)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,160,Auto"), ColumnSpacing = 8 };
            var title = WorkspaceUi.Text(choice.Title); title.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(title);
            var button = new Button { Content = "未绑定", HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                CancelRecording(); button.Focus(); _recording = choice.Action;
                _status.Text = "请按一个单键，Esc 取消"; UpdateButtons();
            };
            button.LostFocus += (_, _) => { if (_recording == choice.Action) CancelRecording(); };
            _buttons[choice.Action] = button; Grid.SetColumn(button, 1); row.Children.Add(button);
            var clear = WorkspaceUi.IconButton("cancel", "清除绑定", async () =>
            {
                CancelRecording();
                var next = new Dictionary<PlayerShortcutAction, string>(_bindings);
                if (!next.Remove(choice.Action)) return;
                await SaveAsync(next);
            });
            Grid.SetColumn(clear, 2); row.Children.Add(clear); Children.Add(row);
        }
        Children.Add(_status);
        AddHandler(KeyDownEvent, OnCaptureKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (_capturedKeys.Remove(e.Key)) e.Handled = true; }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => { CancelRecording(); _capturedKeys.Clear(); };
    }

    public void Populate(IReadOnlyDictionary<PlayerShortcutAction, string> bindings)
    { CancelRecording(); _bindings = new(bindings); UpdateButtons(); }

    private void CancelRecording() { _recording = null; _status.Text = ""; UpdateButtons(); }
    private void UpdateButtons()
    {
        foreach (var pair in _buttons)
            pair.Value.Content = _recording == pair.Key ? "请按一个键…" :
                _bindings.TryGetValue(pair.Key, out var key) ? PlayerShortcuts.DisplayKey(key) : "未绑定";
    }

    private async void OnCaptureKey(object? sender, KeyEventArgs e)
    {
        if (_capturedKeys.Contains(e.Key)) { e.Handled = true; return; }
        if (_recording is not { } action) return;
        e.Handled = true; _capturedKeys.Add(e.Key);
        if (e.Key == Key.Escape) { CancelRecording(); return; }
        if (e.KeyModifiers != KeyModifiers.None || !PlayerShortcuts.IsBindable(e.Key))
        { _status.Text = "请选择一个单键；Tab、Enter 和修饰键用于界面操作"; return; }
        var key = e.Key.ToString();
        var conflict = _bindings.FirstOrDefault(pair => pair.Key != action && pair.Value == key);
        if (conflict.Value is not null)
        {
            _status.Text = $"{PlayerShortcuts.DisplayKey(key)} 已绑定到“{PlayerShortcuts.Actions.First(c => c.Action == conflict.Key).Title}”，请先清除该绑定";
            return;
        }
        var next = new Dictionary<PlayerShortcutAction, string>(_bindings) { [action] = key };
        CancelRecording(); await SaveAsync(next);
    }

    private async Task SaveAsync(Dictionary<PlayerShortcutAction, string> bindings)
    {
        _bindings = bindings; UpdateButtons();
        try { await _save(bindings); }
        catch (Exception error) { _status.Text = $"按键设置未保存：{error.Message}"; }
    }
}
