using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Maps a DPC/ISR execution duration in microseconds to a severity brush, LatencyMon-style: green well under a real-time budget, yellow borderline, red likely to cause audio/USB glitches. Pass ConverterParameter="Background" for the *Bg brushes.</summary>
public sealed class MicrosecondsToSeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var microseconds = value is double d ? d : 0.0;
        var background = string.Equals(parameter as string, "Background", StringComparison.OrdinalIgnoreCase);
        var app = System.Windows.Application.Current;

        var key = microseconds switch
        {
            >= 500 => background ? "DangerBgBrush" : "DangerBrush",
            >= 100 => background ? "WarningBgBrush" : "WarningBrush",
            _ => background ? "SuccessBgBrush" : "SuccessBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
