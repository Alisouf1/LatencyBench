using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Maps a CPU usage percentage to a severity brush — green under load-friendly, red when a core is nearly saturated.</summary>
public sealed class UsageToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0.0;
        var app = System.Windows.Application.Current;
        var key = percent switch
        {
            >= 85 => "DangerBrush",
            >= 60 => "WarningBrush",
            _ => "SuccessBrush",
        };

        return app.FindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
