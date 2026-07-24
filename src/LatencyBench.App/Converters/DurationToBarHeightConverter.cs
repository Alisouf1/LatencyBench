using System.Globalization;
using System.Windows.Data;

namespace LatencyBench.App.Converters;

/// <summary>
/// Scales a microsecond duration to a bar height for the DPC/ISR history strip, on a log scale:
/// real DPC/ISR durations span ~1 µs to ~1000 µs, so a linear scale collapses the overwhelmingly
/// common 1-10 µs events onto the minimum height and renders the whole strip as a flat line
/// (confirmed against a real 235k-sample trace). Log spreads that range out: 1 µs is a stub,
/// 10 µs a third, 100 µs two thirds, 1000 µs full height.
/// </summary>
public sealed class DurationToBarHeightConverter : IValueConverter
{
    private const double MaxBarHeight = 60.0;
    private const double MinBarHeight = 3.0;

    /// <summary>log10 of the top of the displayed range (1000 µs) — durations past this cap out.</summary>
    private const double LogRange = 3.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var microseconds = value is double d ? d : 0.0;
        var normalized = Math.Log10(Math.Max(microseconds, 1.0)) / LogRange;
        return Math.Clamp(normalized * MaxBarHeight, MinBarHeight, MaxBarHeight);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
