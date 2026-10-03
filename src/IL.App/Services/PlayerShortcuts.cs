using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using IL.Core.Settings;

namespace IL.App.Services;

internal static class PlayerShortcuts
{
    public static bool AllowsSource(Control source, Control window, Control workspace)
    {
        var ancestors = source.GetVisualAncestors().Prepend(source).ToArray();
        return (ReferenceEquals(source, window) || ancestors.Contains(workspace)) &&
            !ancestors.Any(c => c is TextBox or NumericUpDown);
    }

    internal sealed record ActionChoice(PlayerShortcutAction Action, string Title);
    public static IReadOnlyList<ActionChoice> Actions { get; } =
    [
        new(PlayerShortcutAction.TogglePlayback, "播放／暂停"),
        new(PlayerShortcutAction.PreviousCue, "上一句"),
        new(PlayerShortcutAction.NextCue, "下一句"),
        new(PlayerShortcutAction.PreviousQuestion, "上一题"),
        new(PlayerShortcutAction.NextQuestion, "下一题"),
        new(PlayerShortcutAction.ReplayCue, "重听当前句"),
        new(PlayerShortcutAction.ToggleSubtitles, "显示／隐藏字幕")
    ];

    public static bool IsBindable(Key key) => Enum.IsDefined(key) && key is not
        (Key.None or Key.Tab or Key.Escape or Key.Return or Key.LeftShift or Key.RightShift or
         Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or
         Key.System or Key.DeadCharProcessed);

    public static bool TryGetAction(IReadOnlyDictionary<PlayerShortcutAction, string> bindings,
        Key key, KeyModifiers modifiers, out PlayerShortcutAction action)
    {
        action = default;
        if (modifiers != KeyModifiers.None || !IsBindable(key)) return false;
        foreach (var binding in bindings)
            if (Enum.IsDefined(binding.Key) && Enum.TryParse<Key>(binding.Value, out var stored) && stored == key)
            { action = binding.Key; return true; }
        return false;
    }

    public static string DisplayKey(string key) => key == nameof(Key.Space) ? "Space（空格）" : key;
}
