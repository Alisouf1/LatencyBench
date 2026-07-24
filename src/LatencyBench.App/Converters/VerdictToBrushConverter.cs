using System.Globalization;
using System.Windows.Data;
using LatencyBench.Core.Advisor;

namespace LatencyBench.App.Converters;

/// <summary>Colors a before/after comparison by its verdict. Pass ConverterParameter="Background" for the *Bg brushes.</summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var background = string.Equals(parameter as string, "Background", StringComparison.OrdinalIgnoreCase);
        var app = System.Windows.Application.Current;

        var key = (value as ComparisonVerdict?) switch
        {
            ComparisonVerdict.Improved => background ? "SuccessBgBrush" : "SuccessBrush",
            ComparisonVerdict.Regressed => background ? "DangerBgBrush" : "DangerBrush",
            _ => background ? "SurfaceHoverBrush" : "TextSecondaryBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
