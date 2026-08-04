using System;
using LatencyBench.Core.Benchmarking;
using LatencyBench.Core.Benchmarking.Models;

namespace LatencyBench.Tests;

public class BenchmarkComparerTests
{
	private static BenchmarkSnapshot Snapshot(
		double avgDpc = 1.0,
		double peakDpc = 2.0,
		double avgInterrupt = 1.0,
		double peakInterrupt = 2.0,
		double avgProcessor = 20.0,
		double avgMemory = 70.0) => new(
			DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8), SampleCount: 16,
			avgDpc, peakDpc, avgInterrupt, peakInterrupt, avgProcessor, avgMemory);

	[Fact]
	public void IdenticalSnapshotsShowNoMeasurableChangeOnEveryMetric()
	{
		var snapshot = Snapshot();

		var result = BenchmarkComparer.Compare(snapshot, snapshot);

		Assert.All(result.Metrics, m => Assert.Equal(MetricVerdict.NoMeasurableChange, m.Verdict));
		Assert.Contains("No measurable difference", result.Headline);
	}

	[Fact]
	public void ALargeSustainedDropInDpcTimeIsReportedAsImproved()
	{
		var before = Snapshot(avgDpc: 8.0, peakDpc: 15.0);
		var after = Snapshot(avgDpc: 2.0, peakDpc: 4.0);

		var result = BenchmarkComparer.Compare(before, after);

		var dpc = Assert.Single(result.Metrics, m => m.Name == "Average DPC time");
		Assert.Equal(MetricVerdict.Improved, dpc.Verdict);
		Assert.Contains("Suggests a real improvement", result.Headline);
	}

	[Fact]
	public void ALargeSustainedRiseInDpcTimeIsReportedAsWorse()
	{
		var before = Snapshot(avgDpc: 2.0);
		var after = Snapshot(avgDpc: 8.0);

		var result = BenchmarkComparer.Compare(before, after);

		var dpc = Assert.Single(result.Metrics, m => m.Name == "Average DPC time");
		Assert.Equal(MetricVerdict.Worse, dpc.Verdict);
		Assert.Contains("worse", result.Headline);
	}

	[Fact]
	public void TinyPercentagePointMovesAreNotReportedDespiteALargeRelativeChange()
	{
		// 0.1% to 0.3% is a "200% increase" that means nothing — both readings are noise-level.
		var before = Snapshot(avgDpc: 0.1);
		var after = Snapshot(avgDpc: 0.3);

		var result = BenchmarkComparer.Compare(before, after);

		var dpc = Assert.Single(result.Metrics, m => m.Name == "Average DPC time");
		Assert.Equal(MetricVerdict.NoMeasurableChange, dpc.Verdict);
	}

	[Fact]
	public void ARelativeChangeBelowTheNoiseToleranceIsNotReported()
	{
		// 5.0 -> 5.5 is a real absolute move (0.5pp) but only 10% relative, under the 20% bar.
		var before = Snapshot(avgDpc: 5.0);
		var after = Snapshot(avgDpc: 5.5);

		var result = BenchmarkComparer.Compare(before, after);

		var dpc = Assert.Single(result.Metrics, m => m.Name == "Average DPC time");
		Assert.Equal(MetricVerdict.NoMeasurableChange, dpc.Verdict);
	}

	[Fact]
	public void AvailableMemoryIsHigherIsBetter()
	{
		// Same magnitude of change as the DPC case above, opposite direction of "better".
		var before = Snapshot(avgMemory: 40.0);
		var after = Snapshot(avgMemory: 70.0);

		var result = BenchmarkComparer.Compare(before, after);

		var memory = Assert.Single(result.Metrics, m => m.Name == "Available memory");
		Assert.Equal(MetricVerdict.Improved, memory.Verdict);
	}

	[Fact]
	public void MemoryDroppingIsWorse()
	{
		var before = Snapshot(avgMemory: 70.0);
		var after = Snapshot(avgMemory: 30.0);

		var result = BenchmarkComparer.Compare(before, after);

		var memory = Assert.Single(result.Metrics, m => m.Name == "Available memory");
		Assert.Equal(MetricVerdict.Worse, memory.Verdict);
	}

	[Fact]
	public void MixedResultsProduceAMixedHeadline()
	{
		// DPC gets much worse, memory gets much better in the same run.
		var before = Snapshot(avgDpc: 1.0, avgMemory: 30.0);
		var after = Snapshot(avgDpc: 9.0, avgMemory: 80.0);

		var result = BenchmarkComparer.Compare(before, after);

		Assert.Contains("Mixed result", result.Headline);
	}

	[Fact]
	public void PeakAndAverageAreJudgedIndependently()
	{
		// Average is unchanged but the peak came down a lot — a single misbehaving spike was fixed
		// without changing the sustained load, and that must show up as its own verdict.
		var before = Snapshot(avgDpc: 2.0, peakDpc: 20.0);
		var after = Snapshot(avgDpc: 2.0, peakDpc: 3.0);

		var result = BenchmarkComparer.Compare(before, after);

		var avg = Assert.Single(result.Metrics, m => m.Name == "Average DPC time");
		var peak = Assert.Single(result.Metrics, m => m.Name == "Peak DPC time");

		Assert.Equal(MetricVerdict.NoMeasurableChange, avg.Verdict);
		Assert.Equal(MetricVerdict.Improved, peak.Verdict);
	}

	[Fact]
	public void EveryMetricSummaryIsNonEmpty()
	{
		var result = BenchmarkComparer.Compare(Snapshot(avgDpc: 1.0), Snapshot(avgDpc: 9.0));

		Assert.All(result.Metrics, m => Assert.False(string.IsNullOrWhiteSpace(m.Summary)));
	}

	[Fact]
	public void ComparisonRetainsTheOriginalSnapshots()
	{
		var before = Snapshot(avgDpc: 1.0);
		var after = Snapshot(avgDpc: 9.0);

		var result = BenchmarkComparer.Compare(before, after);

		Assert.Same(before, result.Before);
		Assert.Same(after, result.After);
	}

	[Fact]
	public void ExactlyFiveMetricsAreAlwaysProduced()
	{
		var result = BenchmarkComparer.Compare(Snapshot(), Snapshot());

		Assert.Equal(5, result.Metrics.Count);
	}
}
