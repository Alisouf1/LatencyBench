using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.HidTesting;

public static class IntervalHistogramBuilder
{
	private static readonly (double UpperMultiple, string Label, bool IsLate)[] Bands = new(double, string, bool)[5]
	{
		(0.5, "< 0.5x", false),
		(1.5, "on time", false),
		(2.0, "1.5-2x late", true),
		(3.0, "2-3x (dropped)", true),
		(double.PositiveInfinity, "> 3x", true)
	};

	public static List<HistogramBucket> Build(IReadOnlyList<double> activeIntervalsMs, double medianMs)
	{
		if (activeIntervalsMs.Count == 0 || medianMs <= 0.0)
		{
			return new List<HistogramBucket>();
		}
		int[] counts = new int[Bands.Length];
		foreach (double activeIntervalsM in activeIntervalsMs)
		{
			double num = activeIntervalsM / medianMs;
			for (int i = 0; i < Bands.Length; i++)
			{
				if (num < Bands[i].UpperMultiple)
				{
					counts[i]++;
					break;
				}
			}
		}
		int max = counts.Max();
		return Bands.Select(((double UpperMultiple, string Label, bool IsLate) band, int num2) => new HistogramBucket(band.Label, counts[num2], (max > 0) ? ((double)counts[num2] / (double)max) : 0.0, band.IsLate)).ToList();
	}
}
