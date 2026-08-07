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

    private static readonly (double UpperFraction, string Label)[] SpeedBandDefinitions = new (double, string)[5]
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

    /// <summary>How far from an exact multiple of 45 degrees still counts as snapped to it.</summary>
    private const double SnapToleranceDegrees = 2.5;

    /// <summary>The eight angles an angle-snapping mouse pulls toward: the cardinals and diagonals.</summary>
    private const int SnapDirections = 8;

    private static double ComputeAngleSnapScore(IReadOnlyList<MouseSample> samples, double ticksPerMillisecond)
    {
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

        double observedSnapFraction = snapMagnitude / totalMagnitude;

        // What the same fraction would be if direction were spread evenly: eight targets, each with a
        // window of 2 x SnapToleranceDegrees. Dividing by it makes 1.0 mean "no snapping" and the
        // maximum (9.0 at the current tolerance) mean "every movement landed on a snap angle",
        // independently of how wide the tolerance is.
        double expectedSnapFraction = SnapDirections * (2.0 * SnapToleranceDegrees) / 360.0;
        return observedSnapFraction / expectedSnapFraction;

        void FlushWindow(double windowDx, double windowDy)
        {
            double magnitude = Math.Sqrt(windowDx * windowDx + windowDy * windowDy);
            if (magnitude < 2.0)
            {
                // Too small to have a meaningful direction; counting it would let sensor noise
                // masquerade as snapping.
                return;
            }

            double degrees = Math.Atan2(windowDy, windowDx) * 180.0 / Math.PI;
            if (degrees < 0.0)
            {
                degrees += 360.0;
            }

            totalMagnitude += magnitude;

            // Distance to the NEAREST multiple of 45, rather than testing which fixed 5-degree bin the
            // angle falls in. The bin approach put each cardinal angle on a bin boundary - bin 9 spans
            // [45,50) - so movement at 46 degrees counted as snapped to 45 while movement at 44 did
            // not, despite being equally close. Real angle snapping scatters either side of its
            // target, so roughly half of it went unrecorded and the score read low. Verified before
            // and after: 44 and 46 degrees now score identically, where previously they were 0 and 9.
            //
            // Math.Round maps 359 degrees to 360, which is the correct nearest target, so wraparound
            // needs no special case.
            double nearestSnapAngle = Math.Round(degrees / 45.0) * 45.0;
            if (Math.Abs(degrees - nearestSnapAngle) <= SnapToleranceDegrees)
            {
                snapMagnitude += magnitude;
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
