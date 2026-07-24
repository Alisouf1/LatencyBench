using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Visible when the bound count is zero (used for empty-state panels), Collapsed otherwise. Pass ConverterParameter="Invert" to flip it.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value switch
        {
            int i => i == 0,
            long l => l == 0,
            null => true,
            _ => false,
        };

        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return (isZero ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
