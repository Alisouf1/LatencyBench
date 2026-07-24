using System.Globalization;
using System.Windows.Data;
using LatencyBench.Core.Models;

namespace LatencyBench.App.Converters;

/// <summary>Friendly badge text for a PortRank — everything but NotApplicable is just its own name.</summary>
public sealed class RankLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as PortRank? == PortRank.NotApplicable ? "N/A" : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
