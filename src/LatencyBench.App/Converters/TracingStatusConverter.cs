using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

public sealed class TracingStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Tracing live." : "Stopped — showing the last trace.";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
