using System.Collections.Generic;

namespace LatencyBench.Core.Benchmarking.Models;

public enum MetricVerdict
{
	/// <summary>The change is inside normal run-to-run noise — nothing worth reporting as real.</summary>
	NoMeasurableChange,

	Improved,

	Worse
}

/// <summary>One metric's before/after comparison, with the arithmetic that produced the verdict
/// kept alongside it rather than only the conclusion.</summary>
public sealed record MetricComparison(
	string Name,
	double Before,
	double After,
	double PercentChange,
	MetricVerdict Verdict,
	string Summary);

/// <summary>
/// The full before/after picture for one applied change. Mirrors the tone of
/// <see cref="Advisor.DpcIsrComparisonGenerator"/> deliberately: conservative thresholds, "suggests"
/// rather than "proves", and every non-finding stated as plainly as every finding.
/// </summary>
public sealed class BenchmarkComparisonResult
{
	public required BenchmarkSnapshot Before { get; init; }

	public required BenchmarkSnapshot After { get; init; }

	public required IReadOnlyList<MetricComparison> Metrics { get; init; }

	/// <summary>A one-line overall read, for display without expanding every metric.</summary>
	public required string Headline { get; init; }
}
