using System;
using System.Collections.Generic;

namespace LatencyBench.Core.HidTesting.Models;

public sealed class HidTestResult
{
    public required string DeviceFriendlyName { get; init; }

    public required string PortLabel { get; init; }

    public required string PortLocation { get; init; }

    public required int SampleCount { get; init; }

    public required double PollingRateHz { get; init; }

    public required double JitterMs { get; init; }

    /// <summary>Median time between consecutive received reports — a report-spacing measurement, not end-to-end input latency (there is no independent physical-event timestamp to measure that against).</summary>
    public required double MedianReportIntervalMs { get; init; }

    public required double EffectivePollingRateHz { get; init; }

    public required double LateReportPercent { get; init; }

    public IReadOnlyList<double> ActiveIntervalsMs { get; init; } = Array.Empty<double>();
}
