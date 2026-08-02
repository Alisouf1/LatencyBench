using System;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.Core.Monitoring;

/// <summary>
/// Turns a stream of readings for one metric into raise/clear warnings, with hysteresis.
/// <para>
/// Two things make this "intelligent" rather than a bare threshold check, and both exist to stop
/// the panel crying wolf:
/// </para>
/// <list type="bullet">
/// <item>
/// A single spike does not raise a warning. DPC time briefly hitting 40% because a driver loaded is
/// normal; the metric has to stay over the threshold for several consecutive samples before it is
/// reported, which is a real, sustained condition rather than noise.
/// </item>
/// <item>
/// Raising and clearing use different thresholds (hysteresis). Without a gap, a metric sitting
/// exactly at the line flickers a warning on and off every sample, which is worse than no warning
/// at all — real information drowned in noise the user learns to ignore.
/// </item>
/// </list>
/// </summary>
public sealed class MetricWatcher
{
    private readonly WarningMetric _metric;
    private readonly double _raiseThreshold;
    private readonly double _clearThreshold;
    private readonly int _consecutiveToRaise;
    private readonly int _consecutiveToClear;
    private readonly WarningSeverity _severity;
    private readonly Func<double, string> _describeRaise;
    private readonly Func<double, string> _describeClear;

    private int _overCount;
    private int _underCount;

    public bool IsAlarmed { get; private set; }

    public MetricWatcher(
        WarningMetric metric,
        double raiseThreshold,
        double clearThreshold,
        int consecutiveToRaise,
        int consecutiveToClear,
        WarningSeverity severity,
        Func<double, string> describeRaise,
        Func<double, string> describeClear)
    {
        _metric = metric;
        _raiseThreshold = raiseThreshold;
        _clearThreshold = clearThreshold;
        _consecutiveToRaise = Math.Max(1, consecutiveToRaise);
        _consecutiveToClear = Math.Max(1, consecutiveToClear);
        _severity = severity;
        _describeRaise = describeRaise;
        _describeClear = describeClear;
    }

    /// <summary>Feeds one reading in. Returns a warning only on the sample that actually crosses a
    /// debounced boundary — every other sample returns null, including every sample while already
    /// alarmed and still over threshold.</summary>
    public MonitoringWarning? Observe(double value, DateTimeOffset timestamp)
    {
        if (!IsAlarmed)
        {
            _overCount = value >= _raiseThreshold ? _overCount + 1 : 0;

            if (_overCount < _consecutiveToRaise)
            {
                return null;
            }

            IsAlarmed = true;
            _underCount = 0;
            return new MonitoringWarning(timestamp, _metric, _severity, _describeRaise(value), IsRecovery: false);
        }

        _underCount = value <= _clearThreshold ? _underCount + 1 : 0;

        if (_underCount < _consecutiveToClear)
        {
            return null;
        }

        IsAlarmed = false;
        _overCount = 0;
        return new MonitoringWarning(timestamp, _metric, WarningSeverity.Info, _describeClear(value), IsRecovery: true);
    }
}
