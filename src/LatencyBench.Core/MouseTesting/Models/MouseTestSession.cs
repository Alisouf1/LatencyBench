using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.MouseTesting.Models;

public sealed class MouseTestSession
{
	public const int MaxTracePoints = 3000;

	public DateTime SavedAt { get; init; } = DateTime.Now;

	public required string DeviceFriendlyName { get; init; }

	public double? Cpi { get; init; }

	public required int SampleCount { get; init; }

	public required double DurationMs { get; init; }

	public required double EffectiveReportRateHz { get; init; }

	public required double TotalPathCounts { get; init; }

	public required double NetDisplacementCounts { get; init; }

	public required double PeakSpeedCountsPerMs { get; init; }

	public required double AngleSnapScore { get; init; }

	public IReadOnlyList<SpeedGainBand> SpeedGainBands { get; init; } = Array.Empty<SpeedGainBand>();

	public IReadOnlyList<MouseSample> Trace { get; init; } = Array.Empty<MouseSample>();

	public static MouseTestSession FromResult(MouseTestResult result, double? cpiOverride = null, int maxTracePoints = 3000)
	{
		return new MouseTestSession
		{
			DeviceFriendlyName = result.DeviceFriendlyName,
			Cpi = (cpiOverride ?? result.Cpi),
			SampleCount = result.SampleCount,
			DurationMs = result.DurationMs,
			EffectiveReportRateHz = result.EffectiveReportRateHz,
			TotalPathCounts = result.TotalPathCounts,
			NetDisplacementCounts = result.NetDisplacementCounts,
			PeakSpeedCountsPerMs = result.PeakSpeedCountsPerMs,
			AngleSnapScore = result.AngleSnapScore,
			SpeedGainBands = result.SpeedGainBands,
			Trace = Downsample(result.Samples, maxTracePoints)
		};
	}

	private static List<MouseSample> Downsample(IReadOnlyList<MouseSample> samples, int maxPoints)
	{
		if (samples.Count <= maxPoints || maxPoints <= 0)
		{
			return samples.ToList();
		}
		List<MouseSample> list = new List<MouseSample>(maxPoints);
		double num = (double)samples.Count / (double)maxPoints;
		for (int i = 0; i < maxPoints; i++)
		{
			list.Add(samples[(int)((double)i * num)]);
		}
		return list;
	}
}
