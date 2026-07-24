using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LatencyBench.Core.Models;

namespace LatencyBench.App.Converters;

/// <summary>For a composite/hub node, joins the recognizable device names found beneath it (e.g. "HID Keyboard Device, HID-compliant mouse"). Empty for leaf nodes, which already show their own real name. Pass ConverterParameter="Visibility" to get Visible/Collapsed instead of the text.</summary>
public sealed class DescendantDeviceNamesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var summary = BuildSummary(value);
        var wantsVisibility = string.Equals(parameter as string, "Visibility", StringComparison.OrdinalIgnoreCase);
        if (wantsVisibility)
        {
            return string.IsNullOrEmpty(summary) ? Visibility.Collapsed : Visibility.Visible;
        }

        return summary;
    }

    private static string BuildSummary(object? value)
    {
        if (value is not UsbDeviceNode node || node.Children.Count == 0)
        {
            return string.Empty;
        }

        var names = node.Children.Select(c => c.GetBestDisplayName()).Distinct().ToList();
        return names.Count == 0 ? string.Empty : $"contains: {string.Join(", ", names)}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
