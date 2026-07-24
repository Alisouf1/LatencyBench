using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

public sealed class SaveLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Saved ✓" : "Save result";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
