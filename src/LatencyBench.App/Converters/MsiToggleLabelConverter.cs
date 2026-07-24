using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

public sealed class MsiToggleLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Disable MSI" : "Enable MSI";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
