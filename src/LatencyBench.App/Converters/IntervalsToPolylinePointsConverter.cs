using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LatencyBench.App.Converters;

/// <summary>Turns a sequence of millisecond intervals into a PointCollection for a Polyline — a real connected line graph, not discrete bars. Fixed 64px height, 8px horizontal spacing per sample.</summary>
public sealed class IntervalsToPolylinePointsConverter : IValueConverter
{
    private const double Height = 64;
    private const double Spacing = 8;

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
            if (item is double ms)
            {
                var y = Height - Math.Clamp(ms * 8.0, 2.0, Height);
                points.Add(new Point(index * Spacing, y));
                index++;
            }
        }

        return points;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
