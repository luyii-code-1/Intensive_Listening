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
        var note = new TextBlock { Text = "该设置由您的组织管理", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C49520")), Margin = new Thickness(0, 2, 0, 0) };
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), VerticalAlignment = content.VerticalAlignment };
        panel.Children.Add(content); Grid.SetRow(note, 1); panel.Children.Add(note);
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
