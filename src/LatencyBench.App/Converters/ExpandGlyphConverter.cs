using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>Maps an expanded/collapsed bool to a plain text disclosure triangle.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
