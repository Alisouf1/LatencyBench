using System;
using System.Collections.Generic;

namespace LatencyBench.Core.MouseTesting.Models;

public sealed class MouseTestResult
{
    public required string DeviceFriendlyName { get; init; }

    public required IReadOnlyList<MouseSample> Samples { get; init; }

    public double? Cpi { get; init; }

    public required int SampleCount { get; init; }

    public required double DurationMs { get; init; }

    public required double EffectiveReportRateHz { get; init; }

    public required double TotalPathCounts { get; init; }

    public required double NetDisplacementCounts { get; init; }

    public required double PeakSpeedCountsPerMs { get; init; }

    public required IReadOnlyList<int> SpikeSampleIndices { get; init; }

    public required double AngleSnapScore { get; init; }

    public IReadOnlyList<SpeedGainBand> SpeedGainBands { get; init; } = Array.Empty<SpeedGainBand>();
}
