using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Scales a millisecond HID report interval to a bar height in pixels for the live port-test graph. Fixed scale — 4px floor, 60px cap around 8ms (125 Hz).</summary>
public sealed class IntervalToBarHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var milliseconds = value is double d ? d : 0.0;
        return Math.Clamp(milliseconds * 8.0, 4.0, 60.0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
