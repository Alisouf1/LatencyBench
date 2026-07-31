using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.App.Converters;

/// <summary>Maps a monitoring warning's severity (and recovery state) to a themed brush key already
/// defined in AppTheme.xaml, matching how the rest of the app colours severity.</summary>
public sealed class WarningSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            WarningSeverity.Critical => "DangerBrush",
            WarningSeverity.Warning => "WarningBrush",
            _ => "SuccessBrush",
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
