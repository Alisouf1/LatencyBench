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

	public required double ReportLatencyMs { get; init; }

	public required double EffectivePollingRateHz { get; init; }

	public required double LateReportPercent { get; init; }

	public IReadOnlyList<double> ActiveIntervalsMs { get; init; } = Array.Empty<double>();
}
