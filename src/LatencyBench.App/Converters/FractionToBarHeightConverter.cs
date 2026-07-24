using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Scales a 0-1 fraction (relative to the largest bucket) to a histogram bar height, with a visible stub for empty buckets.</summary>
public sealed class FractionToBarHeightConverter : IValueConverter
{
    private const double MaxBarHeight = 54.0;
    private const double MinBarHeight = 2.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? d : 0.0;
        return Math.Max(Math.Clamp(fraction, 0, 1) * MaxBarHeight, MinBarHeight);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
