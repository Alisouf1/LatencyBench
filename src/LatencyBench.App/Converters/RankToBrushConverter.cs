using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LatencyBench.Core.Models;

namespace LatencyBench.App.Converters;

/// <summary>Maps a PortRank to its badge background or foreground brush. Pass ConverterParameter="Foreground" for text color.</summary>
public sealed class RankToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var foreground = string.Equals(parameter as string, "Foreground", StringComparison.OrdinalIgnoreCase);
        var app = System.Windows.Application.Current;

        var key = (value as PortRank?) switch
        {
            PortRank.Excellent or PortRank.Good => foreground ? "SuccessBrush" : "SuccessBgBrush",
            PortRank.Fair => foreground ? "WarningBrush" : "WarningBgBrush",
            PortRank.Poor => foreground ? "DangerBrush" : "DangerBgBrush",
            // NotTested and NotApplicable both fall here: neutral, not alarming — NotApplicable in
            // particular must never render as Danger, since it means "this grade doesn't apply", not "bad".
            _ => foreground ? "TextSecondaryBrush" : "SurfaceHoverBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
