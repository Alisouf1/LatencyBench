using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LatencyBench.App.Converters;

/// <summary>Turns a bounded sequence of 0-100 percentages into a PointCollection for a Polyline —
/// used for the monitoring sparklines. Fixed 48px height, 4px horizontal spacing per sample.</summary>
public sealed class PercentSeriesToPolylinePointsConverter : IValueConverter
{
    private const double Height = 48;
    private const double Spacing = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is not IEnumerable values)
        {
            return points;
        }

        var index = 0;
        foreach (var item in values)
        {
            if (item is double percent)
            {
                var clamped = Math.Clamp(percent, 0.0, 100.0);
                var y = Height - (clamped / 100.0 * Height);
                points.Add(new Point(index * Spacing, y));
                index++;
            }
        }

        return points;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
