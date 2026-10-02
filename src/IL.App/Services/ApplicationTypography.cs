using Avalonia;
using Avalonia.Media;
using IL.App.Views.Dialogs;
using IL.Core.Settings;

namespace IL.App.Services;

internal static class ApplicationTypography
{
    public static FontFamily ResolveFont(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ApplicationFontFamily) &&
        FontManager.Current.SystemFonts.Any(font => font.Name == settings.ApplicationFontFamily)
            ? new FontFamily(settings.ApplicationFontFamily) : WorkspaceUi.BodyFont;

    public static void Apply(AppSettings settings)
    {
        if (Application.Current is not { } app) return;
        var font = ResolveFont(settings);
        app.Resources["WorkspaceFont"] = font;
        app.Resources["ContentControlThemeFontFamily"] = font;
        app.Resources["WorkspaceFontWeight"] = (FontWeight)settings.ApplicationFontWeight;
    }
}
