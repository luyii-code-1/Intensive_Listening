using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace IL.App.Views.Dialogs;

internal static class AppBrand
{
    // This PNG is the unchanged 256 px frame from the original Windows app icon.
    private static readonly Lazy<Bitmap> Logo = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://IL.App/Assets/app_logo.png"));
        return new Bitmap(stream);
    });
    internal static Bitmap Image => Logo.Value;
    internal static WindowIcon WindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://IL.App/Assets/app_icon.ico"));
        return new WindowIcon(stream);
    }
}
