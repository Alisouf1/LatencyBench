using System.Globalization;
using System.Windows.Data;
using LatencyBench.Core.Advisor;

namespace LatencyBench.App.Converters;

/// <summary>Maps a FindingSeverity to its badge background or foreground brush. Pass ConverterParameter="Foreground" for text color.</summary>
public sealed class FindingSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var foreground = string.Equals(parameter as string, "Foreground", StringComparison.OrdinalIgnoreCase);
        var app = System.Windows.Application.Current;

        var key = (value as FindingSeverity?) switch
        {
            FindingSeverity.Critical => foreground ? "DangerBrush" : "DangerBgBrush",
            FindingSeverity.Warning => foreground ? "WarningBrush" : "WarningBgBrush",
            _ => foreground ? "TextSecondaryBrush" : "SurfaceHoverBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
