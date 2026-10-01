using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using IL.App.Views;

namespace IL.UiSnapshots;

internal static partial class Program
{
    private static async Task CheckListMotionAsync(WorkspaceContentHost host)
    {
        var panel = new StackPanel { Spacing = 8 };
        host.Content = panel; await Task.Delay(650);
        var type = AppAssembly.GetType("IL.App.Views.SpringListCollection`1", true)!.MakeGenericType(typeof(Control));
        var collection = Activator.CreateInstance(type, panel, panel.Children, (Func<Border, Control>)(frame => frame), (Action<int, int>)panel.Children.Move)!;
        var update = type.GetMethod("Update")!;
        var content = new Dictionary<string, Control>();
        foreach (var key in new[] { "a", "b", "c" }) content[key] = new Border { Height = 48, Background = Brushes.LightBlue, Child = new TextBlock { Text = key } };
        void Present(params string[] keys) => update.Invoke(collection, [keys.Select(key => (key, content[key]))]);
        Border Row(string key) => panel.Children.OfType<Border>().Single(row => (string?)row.Tag == key);
        static double Y(Border row) => ((TransformGroup)row.RenderTransform!).Children.OfType<TranslateTransform>().Single().Y;
        Present("a", "b"); await Task.Delay(650);
        var a = Row("a"); var b = Row("b");
        Present("c", "a", "b");
        if (Row("c").Opacity != 0) throw new InvalidOperationException("List insertion flashed before layout.");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); await Task.Delay(90);
        if (Row("c").Opacity is <= 0 or >= 1 || Y(a) is >= 0 or <= -56) throw new InvalidOperationException("List insertion did not animate both incoming and displaced rows.");
        await Task.Delay(650);
        Present("b", "c", "a"); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); await Task.Delay(90);
        if (!ReferenceEquals(b, Row("b")) || Y(b) <= 0) throw new InvalidOperationException("List reorder lost identity or skipped its spring.");
        await Task.Delay(650);
        Present("c", "a"); await Task.Delay(70);
        if (b.Opacity is <= 0 or >= 1 || b.IsHitTestVisible) throw new InvalidOperationException("List removal did not fade or retained interaction.");
        Present("b", "c", "a"); await Task.Delay(30); Present("c", "a"); await Task.Delay(30); Present("b", "c", "a");
        await Task.Delay(750);
        if (!ReferenceEquals(b, Row("b")) || b.Opacity != 1 || panel.Children.Count != 3) throw new InvalidOperationException("Interrupted list removal discarded the revived row.");
        Present("c", "a"); await Task.Delay(1450);
        if (panel.Children.Count != 2 || Y(a) != 0 || Y(Row("c")) != 0) throw new InvalidOperationException($"List removal left a gap or an unsettled row: count={panel.Children.Count}, a={Y(a)}, c={Y(Row("c"))}");
        var reduced = AppAssembly.GetType("IL.App.Views.Dialogs.FirstRunWizard", true)!.GetProperty("ReducedMotionOverride", BindingFlags.Static | BindingFlags.NonPublic)!;
        reduced.SetValue(null, (Func<bool>)(() => true));
        Present("b", "c", "a"); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); await Task.Delay(65);
        if (Row("b").Opacity is <= 0 or >= 1 || Y(Row("b")) != 0) throw new InvalidOperationException("Reduced list motion did not retain a gentle fade.");
        await Task.Delay(550);
        reduced.SetValue(null, (Func<bool>)(() => false));
    }
}
