using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Colors a report-timing histogram bar: on-time bands read as the accent, late/dropped bands as a warning.</summary>
public sealed class LateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Application.Current.FindResource(value is true ? "DangerBrush" : "AccentBrush");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
