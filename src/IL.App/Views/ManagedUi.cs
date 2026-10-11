using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IL.Core.Management;
namespace IL.App.Views;

internal static class ManagedUi
{
    public static Control Wrap(Control content, Func<bool> managed)
    {
        var note = new TextBlock { Text = "该设置由您的组织管理", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#C49520")), Margin = new Thickness(18, 0, 18, 6) };
        var panel = new StackPanel { Spacing = 2, Children = { content, note } };
        void Refresh() { var locked = managed(); panel.IsEnabled = !locked; content.Opacity = locked ? .45 : 1; note.IsVisible = locked; }
        var client=ManagedRuntime.Client;
        void Changed()=>Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        Refresh();panel.AttachedToVisualTree+=(_,_)=>{Refresh();if(client is not null)client.Changed+=Changed;};
        panel.DetachedFromVisualTree+=(_,_)=>{if(client is not null)client.Changed-=Changed;};
        return panel;
    }
    public static Control Feature(Control content, string name) => Wrap(content, () => !ManagedRuntime.Allows(name));
    public static Control Setting(Control content, params string[] names) => Wrap(content, () => ManagedRuntime.IsManaged && names.Any(ManagedRuntime.Policy.IsLocked));
}
