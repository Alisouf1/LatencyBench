using System.Globalization;
using System.Windows.Data;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.App.Converters;

/// <summary>Maps a TweakState to its badge background or foreground brush. Pass ConverterParameter="Foreground" for text color.</summary>
public sealed class TweakStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var foreground = string.Equals(parameter as string, "Foreground", StringComparison.OrdinalIgnoreCase);
        var app = System.Windows.Application.Current;

        var key = (value as TweakState?) switch
        {
            TweakState.Applied => foreground ? "SuccessBrush" : "SuccessBgBrush",
            TweakState.NotApplied => foreground ? "TextSecondaryBrush" : "SurfaceHoverBrush",
            _ => foreground ? "WarningBrush" : "WarningBgBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
