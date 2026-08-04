using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Benchmarking.Models;

namespace LatencyBench.Core.Benchmarking;

/// <summary>
/// Turns two snapshots into a verdict per metric.
/// <para>
/// Two floors protect against reading noise as a finding, deliberately mirroring
/// <see cref="Advisor.DpcIsrComparisonGenerator"/>'s conservatism rather than inventing a different
/// bar: an absolute floor, because a percentage change computed on numbers near zero is unstable —
/// 0.1% moving to 0.3% is a "200% increase" that means nothing — and a relative floor, because two
/// runs of real, non-trivial load naturally swing by double-digit percentages against each other
/// even with no setting changed.
/// </para>
/// </summary>
public static class BenchmarkComparer
{
	/// <summary>Below this many percentage points of DPC/interrupt time, the machine is essentially
	/// idle on that metric and any change is noise, not signal.</summary>
	private const double NegligibleFloorPoints = 0.5;

	/// <summary>A change smaller than this many percentage points is not reported as real, regardless
	/// of what the relative percentage says.</summary>
	private const double MinimumAbsoluteDeltaPoints = 0.3;

	/// <summary>Relative-change threshold once both readings are above the negligible floor.</summary>
	private const double NoiseTolerancePercent = 20.0;

	public static BenchmarkComparisonResult Compare(BenchmarkSnapshot before, BenchmarkSnapshot after)
	{
		var metrics = new List<MetricComparison>
		{
			CompareLowerIsBetter("Average DPC time", before.AverageDpcTimePercent, after.AverageDpcTimePercent),
			CompareLowerIsBetter("Peak DPC time", before.PeakDpcTimePercent, after.PeakDpcTimePercent),
			CompareLowerIsBetter("Average interrupt time", before.AverageInterruptTimePercent, after.AverageInterruptTimePercent),
			CompareLowerIsBetter("Peak interrupt time", before.PeakInterruptTimePercent, after.PeakInterruptTimePercent),
			CompareHigherIsBetter("Available memory", before.AverageAvailableMemoryPercent, after.AverageAvailableMemoryPercent)
		};

		return new BenchmarkComparisonResult
		{
			Before = before,
			After = after,
			Metrics = metrics,
			Headline = BuildHeadline(metrics)
		};
	}

	private static MetricComparison CompareLowerIsBetter(string name, double before, double after)
		=> Compare(name, before, after, lowerIsBetter: true);

	private static MetricComparison CompareHigherIsBetter(string name, double before, double after)
		=> Compare(name, before, after, lowerIsBetter: false);

	private static MetricComparison Compare(string name, double before, double after, bool lowerIsBetter)
	{
		double delta = after - before;

		if (Math.Abs(delta) < MinimumAbsoluteDeltaPoints
			|| (before < NegligibleFloorPoints && after < NegligibleFloorPoints))
		{
			return new MetricComparison(
				name, before, after, PercentChange: 0.0, MetricVerdict.NoMeasurableChange,
				$"{name}: {before:0.0}% → {after:0.0}%. No measurable change.");
		}

		double baseline = Math.Max(Math.Abs(before), NegligibleFloorPoints);
		double percentChange = delta / baseline * 100.0;

		if (Math.Abs(percentChange) < NoiseTolerancePercent)
		{
			return new MetricComparison(
				name, before, after, percentChange, MetricVerdict.NoMeasurableChange,
				$"{name}: {before:0.0}% → {after:0.0}% ({Math.Abs(percentChange):0}% change) — within normal " +
				"run-to-run variation.");
		}

		bool improved = lowerIsBetter ? delta < 0 : delta > 0;
		string verb = improved ? "improved" : "got worse";

		return new MetricComparison(
			name, before, after, percentChange,
			improved ? MetricVerdict.Improved : MetricVerdict.Worse,
			$"{name}: {before:0.0}% → {after:0.0}% ({Math.Abs(percentChange):0}% {verb}) — suggests a real change.");
	}

	private static string BuildHeadline(IReadOnlyList<MetricComparison> metrics)
	{
		int improved = metrics.Count(m => m.Verdict == MetricVerdict.Improved);
		int worse = metrics.Count(m => m.Verdict == MetricVerdict.Worse);

		if (improved == 0 && worse == 0)
		{
			return "No measurable difference between before and after — every metric stayed within " +
				   "normal run-to-run variation.";
		}

		if (improved > 0 && worse == 0)
		{
			return $"Suggests a real improvement in {improved} metric(s).";
		}

		if (worse > 0 && improved == 0)
		{
			return $"Suggests {worse} metric(s) got worse — worth checking whether this change was the cause.";
		}

		return $"Mixed result: {improved} metric(s) improved, {worse} got worse.";
	}
}
