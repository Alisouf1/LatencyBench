using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

public sealed class TestingLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Testing…" : "Start test";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
