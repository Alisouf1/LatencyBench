using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LatencyBench.Core.MouseTesting;

namespace LatencyBench.App.Converters;

/// <summary>Turns a sequence of pre-scaled MouseChartBuilder.ChartPoint values into a Polyline PointCollection.</summary>
public sealed class ChartPointsToPolylinePointsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is not IEnumerable values)
        {
            return points;
        }

        foreach (var item in values)
        {
            if (item is ChartPoint p)
            {
                points.Add(new Point(p.X, p.Y));
            }
        }

        return points;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
