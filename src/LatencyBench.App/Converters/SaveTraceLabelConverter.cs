using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

public sealed class SaveTraceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Saved ✓" : "Save trace";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
