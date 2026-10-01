using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task CheckPopupMotionAsync()
    {
        var reduced = AppAssembly.GetType("IL.App.Views.Dialogs.FirstRunWizard", true)!.GetProperty("ReducedMotionOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        reduced.SetValue(null, (Func<bool>)(() => false));
        var row = new Border { Width = 1100, Height = 76, Background = Brushes.White, Child = new TextBlock { Text = "It helps stop things from being thrown away.", FontSize = 22 } };
        var combo = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = 0, Width = 160 };
        var panel = new StackPanel { Margin = new Thickness(24, 120), Spacing = 24, Children = { row, combo } };
        var owner = CreateWindow(panel); owner.Show(); await Task.Delay(500);
        var flyout = new Flyout { Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { new Button { Content = "播放" }, new Button { Content = "播放并暂停" } } }, Placement = PlacementMode.Bottom };
        var pointer = new Point(230, 155); owner.MouseMove(pointer);
        flyout.ShowAt(row, true);
        var popup = owner.OpenedPopups.Single();
        var surface = popup.Child!;
        if (popup.Placement != PlacementMode.Pointer || surface.Opacity != 0) throw new InvalidOperationException("Cue flyout was not pointer anchored or flashed before entrance.");
        await Task.Delay(90);
        var position = owner.PointToClient(surface.PointToScreen(default));
        if (Math.Abs(position.X - pointer.X) > 45 || Math.Abs(position.Y - pointer.Y) > 45) throw new InvalidOperationException($"Flyout did not open near the pointer: {position}, {pointer}");
        if (surface.Opacity is <= 0 or >= 1) throw new InvalidOperationException("Flyout entrance skipped middle frames.");
        SaveMotionFrame(owner, "pointer-popup-mid"); await Task.Delay(600);
        flyout.Hide();
        await CheckExitAsync(owner, () => flyout.IsOpen);
        var playButton = (Button)((StackPanel)flyout.Content!).Children[0]; playButton.Click += (_, _) => flyout.Hide();
        flyout.ShowAt(row, true); await Task.Delay(550);
        var playRoot = TopLevel.GetTopLevel(owner.OpenedPopups.Single().Child!)!;
        var playHit = playButton.TranslatePoint(new Point(12, 12), playRoot)!.Value;
        playRoot.MouseMove(playHit); playRoot.MouseDown(playHit, Avalonia.Input.MouseButton.Left); await Task.Delay(60); playRoot.MouseUp(playHit, Avalonia.Input.MouseButton.Left);
        await CheckExitAsync(owner, () => flyout.IsOpen);
        flyout.ShowAt(row, true); await Task.Delay(550);
        if (playButton.Opacity != 1) throw new InvalidOperationException("Reopened cue action retained its pressed state.");
        flyout.Hide(); await Task.Delay(550);
        flyout.ShowAt(row, false); await Task.Delay(550);
        if (owner.OpenedPopups.Single().Placement != PlacementMode.Bottom) throw new InvalidOperationException("Keyboard flyout did not retain its row anchor.");
        flyout.Hide(); await Task.Delay(550);
        owner.MouseMove(new Point(owner.ClientSize.Width - 6, owner.ClientSize.Height - 6)); flyout.ShowAt(row, true); await Task.Delay(550);
        var edge = owner.OpenedPopups.Single().Child!; var edgePosition = owner.PointToClient(edge.PointToScreen(default));
        if (edgePosition.X + edge.Bounds.Width > owner.ClientSize.Width + 1 || edgePosition.Y + edge.Bounds.Height > owner.ClientSize.Height + 1) throw new InvalidOperationException($"Popup overflowed its usable edge: {edgePosition}, {edge.Bounds}");
        flyout.Hide(); await Task.Delay(550);
        var menu = new FAMenuFlyout { ItemsSource = new[] { new FAMenuFlyoutItem { Text = "播放" }, new FAMenuFlyoutItem { Text = "播放并暂停" } } };
        owner.MouseMove(pointer); menu.ShowAt(row, true); await Task.Delay(75);
        if (owner.OpenedPopups.Single().Child!.Opacity is <= 0 or >= 1) throw new InvalidOperationException("Context menu did not animate.");
        await Task.Delay(550);
        menu.Hide(); await CheckExitAsync(owner, () => menu.IsOpen);
        var executed = false;
        var command = (FAMenuFlyoutItem)menu.ItemsSource!.Cast<object>().First(); command.Click += (_, _) => executed = true;
        menu.ShowAt(row, true); await Task.Delay(550);
        var menuRoot = TopLevel.GetTopLevel(owner.OpenedPopups.Single().Child!)!;
        var hit = command.TranslatePoint(new Point(12, 12), menuRoot)!.Value;
        menuRoot.MouseMove(hit); menuRoot.MouseDown(hit, Avalonia.Input.MouseButton.Left); await Task.Delay(60);
        if (command.Opacity is <= .88 or >= 1) throw new InvalidOperationException("Menu command press did not animate.");
        menuRoot.MouseUp(hit, Avalonia.Input.MouseButton.Left);
        if (!executed) throw new InvalidOperationException("Menu command was delayed or discarded.");
        await CheckExitAsync(owner, () => menu.IsOpen);
        menu.ShowAt(row, true); await Task.Delay(550);
        if (command.Opacity != 1) throw new InvalidOperationException($"Reopened menu retained its pressed state: {command.Opacity}");
        menu.Hide(); await Task.Delay(550);
        combo.IsDropDownOpen = true; await Task.Delay(75);
        if (owner.OpenedPopups.Single().Child!.Opacity is <= 0 or >= 1) throw new InvalidOperationException("Settings dropdown did not animate.");
        await Task.Delay(550); combo.IsDropDownOpen = false; await CheckExitAsync(owner, () => combo.IsDropDownOpen);
        ToolTip.SetTip(row, "句子操作"); ToolTip.SetIsOpen(row, true); await Task.Delay(65);
        if (owner.OpenedPopups.Single().Child!.Opacity is <= 0 or >= 1) throw new InvalidOperationException("Tooltip did not animate.");
        await Task.Delay(550); ToolTip.SetIsOpen(row, false); await CheckExitAsync(owner, () => ToolTip.GetIsOpen(row));
        owner.MouseMove(pointer);
        for (var i = 0; i < 4; i++) { flyout.ShowAt(row, true); await Task.Delay(35); flyout.Hide(); await Task.Delay(20); }
        flyout.ShowAt(row, true);
        if (owner.OpenedPopups.Single().Child!.Opacity <= 0) throw new InvalidOperationException("Popup reopening flashed through an empty frame.");
        await Task.Delay(650);
        if (owner.OpenedPopups.Single().Child!.Opacity != 1 || owner.GetVisualDescendants().Any(c => c.Name == "PopupExitSurface")) throw new InvalidOperationException("Rapid popup reopening left an exit or an incomplete entrance.");
        flyout.Hide(); await Task.Delay(550);
        reduced.SetValue(null, (Func<bool>)(() => true)); owner.MouseMove(pointer); flyout.ShowAt(row, true); await Task.Delay(65);
        var reducedSurface = owner.OpenedPopups.Single().Child!;
        var transform = (TransformGroup)reducedSurface.RenderTransform!;
        if (reducedSurface.Opacity is <= 0 or >= 1 || transform.Children.OfType<TranslateTransform>().Single().Y != 0 || transform.Children.OfType<ScaleTransform>().Single().ScaleX != 1) throw new InvalidOperationException("Reduced popup motion did not use a gentle fade.");
        flyout.Hide(); SaveMotionFrame(owner, "popup-exit-mid");
        await Task.Delay(550);
        if (owner.GetVisualDescendants().Any(c => c.Name == "PopupExitSurface")) throw new InvalidOperationException("Popup exit retained an image surface.");
        reduced.SetValue(null, null); owner.Close();
        await File.WriteAllTextAsync(Path.Combine(_output, "popups.json"), JsonSerializer.Serialize(new { PointerAnchor = "passed", PointerPosition = position.ToString(), EdgePlacement = edgePosition.ToString(), KeyboardAnchor = "passed", Flyout = "spring entrance and noninteractive exit passed", ContextMenu = "passed", ComboBox = "passed", Tooltip = "passed", CommandPressAndImmediateAction = "passed", RapidReopening = "passed", ReducedMotion = "gentle fade passed", Boundary = "Headless popup positioning and rendered animation states; native visual acceptance belongs to the user" }, Json));
    }
    private static async Task CheckExitAsync(Window owner, Func<bool> open)
    {
        if (open()) throw new InvalidOperationException("Menu action waited for animation before dismissing.");
        await Task.Delay(65);
        var exit = owner.GetVisualDescendants().OfType<Border>().SingleOrDefault(c => c.Name == "PopupExitSurface");
        if (exit == null || exit.IsHitTestVisible || exit.Opacity is <= 0 or >= 1) throw new InvalidOperationException($"Popup did not retain a noninteractive spring exit: exists={exit != null}, opacity={exit?.Opacity}, visible={exit?.IsVisible}");
        await Task.Delay(550);
        if (owner.GetVisualDescendants().Any(c => c.Name == "PopupExitSurface")) throw new InvalidOperationException("Popup exit did not clean up.");
    }
}
