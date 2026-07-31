using System;
using System.Collections.Generic;

namespace LatencyBench.Core.HidTesting;

public static class JitterLatencyAnalyzer
{
	/// <param name="MedianReportIntervalMs">
	/// The median time between consecutive received reports — NOT end-to-end input latency. There is
	/// no independent timestamp for the physical input event (button press / motion), only the
	/// arrival time of each report, so this can only measure spacing between reports, never how long
	/// a report took to arrive after the action that produced it.
	/// </param>
	public readonly record struct AnalysisResult(int SampleCount, double PollingRateHz, double JitterMs, double MedianReportIntervalMs, double EffectivePollingRateHz, double LateReportPercent, IReadOnlyList<double> ActiveIntervalsMs);

	/// <summary>An interval longer than this multiple of the median is the user pausing, not the
	/// device being late, and is excluded from every statistic.</summary>
	public const double IdleGapMultiplier = 10.0;

	/// <summary>An interval longer than this multiple of the median counts as a late report.</summary>
	public const double LateReportMultiplier = 1.5;

	/// <summary>An interval shorter than this multiple of the median is a coalescing artifact — two
	/// reports delivered back to back after the driver batched them — not a genuinely faster poll.</summary>
	public const double BurstArtifactMultiplier = 0.5;

	public static AnalysisResult? Analyze(IReadOnlyList<long> timestampTicks, double ticksPerMillisecond)
	{
		if (timestampTicks.Count < 3 || ticksPerMillisecond <= 0.0)
		{
			return null;
		}

		var intervals = new List<double>(timestampTicks.Count - 1);
		for (int i = 1; i < timestampTicks.Count; i++)
		{
			long delta = timestampTicks[i] - timestampTicks[i - 1];
			if (delta > 0)
			{
				intervals.Add((double)delta / ticksPerMillisecond);
			}
		}

		if (intervals.Count == 0)
		{
			return null;
		}

		double medianMs = Median(intervals);
		if (medianMs <= 0.0)
		{
			return null;
		}

		// One band, used for every statistic below. Previously jitter used its own band that had an
		// upper bound but no lower bound, so the sub-median burst artifacts this band exists to
		// exclude were still fed into the standard deviation and inflated the reported jitter — the
		// BurstArtifactMultiplier constant was declared for exactly this and then never applied.
		double lowerBound = medianMs * BurstArtifactMultiplier;
		double upperBound = medianMs * IdleGapMultiplier;

		var active = new List<double>(intervals.Count);
		foreach (double interval in intervals)
		{
			if (interval >= lowerBound && interval <= upperBound)
			{
				active.Add(interval);
			}
		}

		if (active.Count == 0)
		{
			active = intervals;
		}

		double mean = 0.0;
		foreach (double interval in active)
		{
			mean += interval;
		}

		mean /= active.Count;

		double sumOfSquares = 0.0;
		int lateCount = 0;
		double lateThreshold = medianMs * LateReportMultiplier;
		foreach (double interval in active)
		{
			double deviation = interval - mean;
			sumOfSquares += deviation * deviation;
			if (interval > lateThreshold)
			{
				lateCount++;
			}
		}

		double jitterMs = Math.Sqrt(sumOfSquares / active.Count);
		double pollingRateHz = 1000.0 / medianMs;
		double effectivePollingRateHz = (mean > 0.0) ? (1000.0 / mean) : 0.0;
		double lateReportPercent = 100.0 * lateCount / active.Count;

		return new AnalysisResult(timestampTicks.Count, pollingRateHz, jitterMs, medianMs, effectivePollingRateHz, lateReportPercent, active);
	}

	/// <summary>
	/// Sorts a scratch copy in place rather than building a LINQ-ordered sequence and materialising it.
	/// This runs on every live refresh while a test is in progress, so it is on the app's own hot path.
	/// </summary>
	private static double Median(List<double> values)
	{
		double[] scratch = values.ToArray();
		Array.Sort(scratch);
		int middle = scratch.Length / 2;
		return (scratch.Length % 2 == 0)
			? ((scratch[middle - 1] + scratch[middle]) / 2.0)
			: scratch[middle];
	}
}
