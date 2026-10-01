using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FluentAvalonia.UI.Controls;

namespace IL.App.Views.Dialogs;

internal sealed class SpringContentDialog : FAContentDialog
{
    private SpringMotion? _surface;
    protected override Type StyleKeyOverride => typeof(FAContentDialog);
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Border>("BackgroundElement") is { } background)
        {
            _surface = SpringMotion.For(background);
            _surface.Set(0, y: FirstRunWizard.MotionReduced() ? 0 : 12, scale: FirstRunWizard.MotionReduced() ? 1 : .98);
        }
    }
    public SpringContentDialog()
    {
        Opened += (_, _) => { if (_surface != null) _ = _surface.To(); };
        Closing += async (_, args) =>
        {
            if (_surface == null || args.Cancel) return;
            var deferral = args.GetDeferral();
            try { await _surface.To(0, y: 12, scale: .98, response: .22); }
            finally { deferral.Complete(); }
        };
    }
}
