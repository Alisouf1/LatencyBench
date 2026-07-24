using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.HidTesting;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Core.MouseTesting;

public static class MouseMotionAnalyzer
{
	public readonly record struct AnalysisResult(int SampleCount, double DurationMs, double EffectiveReportRateHz, double TotalPathCounts, double NetDisplacementCounts, double PeakSpeedCountsPerMs, IReadOnlyList<int> SpikeSampleIndices, double AngleSnapScore, IReadOnlyList<SpeedGainBand> SpeedGainBands);

	private const double SpikeMedianMultiplier = 5.0;

	private static readonly (double UpperFraction, string Label)[] SpeedBandDefinitions = new(double, string)[5]
	{
		(0.2, "0-20%"),
		(0.4, "20-40%"),
		(0.6, "40-60%"),
		(0.8, "60-80%"),
		(double.PositiveInfinity, "80-100%")
	};

	public static AnalysisResult? Analyze(IReadOnlyList<MouseSample> samples, double ticksPerMillisecond)
	{
		if (samples.Count < 3 || ticksPerMillisecond <= 0.0)
		{
			return null;
		}
		double durationMs = (double)(samples[samples.Count - 1].TimestampTicks - samples[0].TimestampTicks) / ticksPerMillisecond;
		double effectiveReportRateHz = JitterLatencyAnalyzer.Analyze(samples.Select((MouseSample s) => s.TimestampTicks).ToList(), ticksPerMillisecond)?.EffectivePollingRateHz ?? 0.0;
		double num = 0.0;
		double num2 = 0.0;
		double num3 = 0.0;
		double[] array = new double[samples.Count];
		for (int num4 = 0; num4 < samples.Count; num4++)
		{
			int dx = samples[num4].Dx;
			int dy = samples[num4].Dy;
			int num5 = dx;
			num += (double)num5;
			num2 += (double)dy;
			double num6 = Math.Sqrt((double)num5 * (double)num5 + (double)dy * (double)dy);
			num3 += num6;
			if (num4 != 0)
			{
				double num7 = (double)(samples[num4].TimestampTicks - samples[num4 - 1].TimestampTicks) / ticksPerMillisecond;
				array[num4] = ((num7 > 0.0) ? (num6 / num7) : 0.0);
			}
		}
		double netDisplacementCounts = Math.Sqrt(num * num + num2 * num2);
		double num8 = ((array.Length != 0) ? array.Max() : 0.0);
		List<int> spikeSampleIndices = FindSpikes(array);
		double angleSnapScore = ComputeAngleSnapScore(samples, ticksPerMillisecond);
		List<SpeedGainBand> speedGainBands = BuildSpeedGainBands(samples, array, num8);
		return new AnalysisResult(samples.Count, durationMs, effectiveReportRateHz, num3, netDisplacementCounts, num8, spikeSampleIndices, angleSnapScore, speedGainBands);
	}

	private static List<int> FindSpikes(double[] speeds)
	{
		List<double> list = speeds.Where((double s) => s > 0.0).ToList();
		if (list.Count == 0)
		{
			return new List<int>();
		}
		double num = Median(list);
		if (num <= 0.0)
		{
			return new List<int>();
		}
		List<int> list2 = new List<int>();
		for (int num2 = 0; num2 < speeds.Length; num2++)
		{
			if (speeds[num2] > num * 5.0)
			{
				list2.Add(num2);
			}
		}
		return list2;
	}

	private static double ComputeAngleSnapScore(IReadOnlyList<MouseSample> samples, double ticksPerMillisecond)
	{
		HashSet<int> snapBinIndices = new HashSet<int>();
		for (int i = 0; i < 360; i += 45)
		{
			snapBinIndices.Add((int)((double)i / 5.0) % 72);
		}
		double totalMagnitude = 0.0;
		double snapMagnitude = 0.0;
		long timestampTicks = samples[0].TimestampTicks;
		double num = 0.0;
		double num2 = 0.0;
		foreach (MouseSample sample in samples)
		{
			if ((double)(sample.TimestampTicks - timestampTicks) / ticksPerMillisecond > 8.0)
			{
				FlushWindow(num, num2);
				num = 0.0;
				num2 = 0.0;
				timestampTicks = sample.TimestampTicks;
			}
			num += (double)sample.Dx;
			num2 += (double)sample.Dy;
		}
		FlushWindow(num, num2);
		if (totalMagnitude <= 0.0)
		{
			return 0.0;
		}
		double num3 = snapMagnitude / totalMagnitude;
		double num4 = (double)snapBinIndices.Count * 5.0 / 360.0;
		return num3 / num4;
		void FlushWindow(double windowDx, double windowDy)
		{
			double num5 = Math.Sqrt(windowDx * windowDx + windowDy * windowDy);
			if (!(num5 < 2.0))
			{
				double num6 = Math.Atan2(windowDy, windowDx) * 180.0 / Math.PI;
				if (num6 < 0.0)
				{
					num6 += 360.0;
				}
				int item = (int)(num6 / 5.0) % 72;
				totalMagnitude += num5;
				if (snapBinIndices.Contains(item))
				{
					snapMagnitude += num5;
				}
			}
		}
	}

	private static List<SpeedGainBand> BuildSpeedGainBands(IReadOnlyList<MouseSample> samples, double[] speeds, double peakSpeed)
	{
		if (peakSpeed <= 0.0)
		{
			return new List<SpeedGainBand>();
		}
		double[] array = new double[SpeedBandDefinitions.Length];
		int[] array2 = new int[SpeedBandDefinitions.Length];
		for (int i = 1; i < samples.Count; i++)
		{
			if (speeds[i] <= 0.0)
			{
				continue;
			}
			double num = speeds[i] / peakSpeed;
			double num2 = Math.Sqrt((double)samples[i].Dx * (double)samples[i].Dx + (double)samples[i].Dy * (double)samples[i].Dy);
			for (int j = 0; j < SpeedBandDefinitions.Length; j++)
			{
				if (num <= SpeedBandDefinitions[j].UpperFraction)
				{
					array[j] += num2;
					array2[j]++;
					break;
				}
			}
		}
		List<SpeedGainBand> list = new List<SpeedGainBand>();
		for (int k = 0; k < SpeedBandDefinitions.Length; k++)
		{
			if (array2[k] != 0)
			{
				list.Add(new SpeedGainBand(SpeedBandDefinitions[k].Label, array[k] / (double)array2[k], array2[k]));
			}
		}
		return list;
	}

	private static double Median(List<double> values)
	{
		List<double> list = values.OrderBy((double v) => v).ToList();
		int num = list.Count / 2;
		return (list.Count % 2 == 0) ? ((list[num - 1] + list[num]) / 2.0) : list[num];
	}
}
