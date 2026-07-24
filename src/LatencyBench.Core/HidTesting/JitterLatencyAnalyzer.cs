using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.HidTesting;

public static class JitterLatencyAnalyzer
{
	public readonly record struct AnalysisResult(int SampleCount, double PollingRateHz, double JitterMs, double ReportLatencyMs, double EffectivePollingRateHz, double LateReportPercent, IReadOnlyList<double> ActiveIntervalsMs);

	public const double IdleGapMultiplier = 10.0;

	public const double LateReportMultiplier = 1.5;

	public const double BurstArtifactMultiplier = 0.5;

	public static AnalysisResult? Analyze(IReadOnlyList<long> timestampTicks, double ticksPerMillisecond)
	{
		if (timestampTicks.Count < 3 || ticksPerMillisecond <= 0.0)
		{
			return null;
		}
		List<double> list = new List<double>(timestampTicks.Count - 1);
		for (int i = 1; i < timestampTicks.Count; i++)
		{
			long num = timestampTicks[i] - timestampTicks[i - 1];
			if (num > 0)
			{
				list.Add((double)num / ticksPerMillisecond);
			}
		}
		if (list.Count == 0)
		{
			return null;
		}
		double medianMs = Median(list);
		List<double> list2 = list.Where((double v) => v <= medianMs * 2.0).ToList();
		if (list2.Count == 0)
		{
			list2 = list;
		}
		double mean = list2.Average();
		double d = list2.Select((double v) => (v - mean) * (v - mean)).Average();
		double jitterMs = Math.Sqrt(d);
		double pollingRateHz = ((medianMs > 0.0) ? (1000.0 / medianMs) : 0.0);
		List<double> list3 = list.Where((double v) => v >= medianMs * 0.5 && v <= medianMs * 10.0).ToList();
		if (list3.Count == 0)
		{
			list3 = list;
		}
		double num2 = list3.Average();
		double effectivePollingRateHz = ((num2 > 0.0) ? (1000.0 / num2) : 0.0);
		int num3 = list3.Count((double v) => v > medianMs * 1.5);
		double lateReportPercent = 100.0 * (double)num3 / (double)list3.Count;
		return new AnalysisResult(timestampTicks.Count, pollingRateHz, jitterMs, medianMs, effectivePollingRateHz, lateReportPercent, list3);
	}

	private static double Median(List<double> values)
	{
		List<double> list = values.OrderBy((double v) => v).ToList();
		int num = list.Count / 2;
		return (list.Count % 2 == 0) ? ((list[num - 1] + list[num]) / 2.0) : list[num];
	}
}
