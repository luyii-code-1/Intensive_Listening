using Avalonia.Animation.Easings;

namespace IL.App.Views;

/// <summary>Critically damped spring curve for the deliberately timed welcome sequence.</summary>
public sealed class SpringEasing : Easing
{
    public override double Ease(double progress)
    {
        const double frequency = 9;
        var normalization = 1 - (1 + frequency) * Math.Exp(-frequency);
        return (1 - (1 + frequency * progress) * Math.Exp(-frequency * progress)) / normalization;
    }
}
