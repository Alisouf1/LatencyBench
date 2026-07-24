using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Visible when the bound bool is false, Collapsed when true — the inverse of BooleanToVisibilityConverter.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
