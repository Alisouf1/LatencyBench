using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Core.MouseTesting;

public static class MouseChartBuilder
{
    public readonly record struct ChartData(IReadOnlyList<ChartPoint> Points, IReadOnlyList<AxisTick> XTicks, IReadOnlyList<AxisTick> YTicks);

    /// <summary>Blank border kept clear on each edge, as a fraction of the canvas.</summary>
    private const double MarginFraction = 0.08;

    /// <summary>
    /// Rejects a tick rate that cannot produce finite coordinates.
    ///
    /// <para>
    /// The time-based modes divide by this value, and the first sample's elapsed time is always zero,
    /// so a rate of zero yields 0/0 = NaN for the first point and infinity for the rest. Measured what
    /// happens next rather than assuming it: List.Min propagates the NaN while List.Max returns
    /// infinity, so the range becomes NaN and EVERY mapped coordinate is NaN - not just the first.
    /// WPF does not reject a NaN coordinate loudly; it silently draws nothing, or throws inside the
    /// render pass where the cause is no longer recoverable from the stack.
    /// </para>
    ///
    /// <para>
    /// MouseMotionAnalyzer.Analyze already refused a non-positive rate. This is the same guard on the
    /// other consumer of the same input.
    /// </para>
    /// </summary>
    private static bool IsUsableTickRate(double ticksPerMillisecond) =>
        ticksPerMillisecond > 0.0 && double.IsFinite(ticksPerMillisecond);

    public static ChartData? Build(IReadOnlyList<MouseSample> samples, MouseChartMode mode, double width, double height, double ticksPerMillisecond, int tickCount = 5)
    {
        if (samples.Count == 0 || width <= 0.0 || height <= 0.0 || !IsUsableTickRate(ticksPerMillisecond))
        {
            return null;
        }
        List<ChartPoint> list = ToDataSpace(samples, mode, ticksPerMillisecond);
        var (minX, maxX, minY, maxY) = DataBounds(list);
        return MapToPixels(list, minX, maxX, minY, maxY, width, height, tickCount);
    }

    public static (ChartData A, ChartData B)? BuildOverlay(IReadOnlyList<MouseSample> samplesA, IReadOnlyList<MouseSample> samplesB, MouseChartMode mode, double width, double height, double ticksPerMillisecond, int tickCount = 5)
    {
        if ((samplesA.Count == 0 && samplesB.Count == 0) || width <= 0.0 || height <= 0.0
            || !IsUsableTickRate(ticksPerMillisecond))
        {
            return null;
        }
        List<ChartPoint> list = ToDataSpace(samplesA, mode, ticksPerMillisecond);
        List<ChartPoint> list2 = ToDataSpace(samplesB, mode, ticksPerMillisecond);
        List<ChartPoint> list3 = list.Concat(list2).ToList();
        if (list3.Count == 0)
        {
            return null;
        }
        (double MinX, double MaxX, double MinY, double MaxY) tuple = DataBounds(list3);
        double item = tuple.MinX;
        double item2 = tuple.MaxX;
        double item3 = tuple.MinY;
        double item4 = tuple.MaxY;
        ChartData item5 = MapToPixels(list, item, item2, item3, item4, width, height, tickCount);
        ChartData item6 = MapToPixels(list2, item, item2, item3, item4, width, height, tickCount);
        return (item5, item6);
    }

    private static List<ChartPoint> ToDataSpace(IReadOnlyList<MouseSample> samples, MouseChartMode mode, double ticksPerMillisecond)
    {
        if (samples.Count == 0)
        {
            return new List<ChartPoint>();
        }
        long timestampTicks = samples[0].TimestampTicks;
        List<ChartPoint> list = new List<ChartPoint>(samples.Count);
        double num = 0.0;
        double num2 = 0.0;
        foreach (MouseSample sample in samples)
        {
            switch (mode)
            {
                case MouseChartMode.XCountsVsTime:
                    list.Add(new ChartPoint((double)(sample.TimestampTicks - timestampTicks) / ticksPerMillisecond, sample.Dx));
                    break;
                case MouseChartMode.YCountsVsTime:
                    list.Add(new ChartPoint((double)(sample.TimestampTicks - timestampTicks) / ticksPerMillisecond, sample.Dy));
                    break;
                case MouseChartMode.PathXY:
                    num += (double)sample.Dx;
                    num2 += (double)sample.Dy;
                    list.Add(new ChartPoint(num, num2));
                    break;
            }
        }
        return list;
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) DataBounds(List<ChartPoint> points)
    {
        if (points.Count == 0)
        {
            return (MinX: 0.0, MaxX: 1.0, MinY: 0.0, MaxY: 1.0);
        }
        double num = points.Min((ChartPoint p) => p.X);
        double num2 = points.Max((ChartPoint p) => p.X);
        double num3 = points.Min((ChartPoint p) => p.Y);
        double num4 = points.Max((ChartPoint p) => p.Y);
        if (num == num2)
        {
            num -= 1.0;
            num2 += 1.0;
        }
        if (num3 == num4)
        {
            num3 -= 1.0;
            num4 += 1.0;
        }
        num3 = Math.Min(num3, 0.0);
        num4 = Math.Max(num4, 0.0);
        return (MinX: num, MaxX: num2, MinY: num3, MaxY: num4);
    }

    private static ChartData MapToPixels(List<ChartPoint> dataPoints, double minX, double maxX, double minY, double maxY, double width, double height, int tickCount)
    {
        double xRange = maxX - minX;
        double yRange = maxY - minY;
        // MarginFraction was declared but never referenced while both sites hardcoded the same
        // literal, so changing the constant would have silently done nothing.
        double marginX = width * MarginFraction;
        double marginY = height * MarginFraction;
        double plotWidth = width - marginX * 2.0;
        double plotHeight = height - marginY * 2.0;
        List<ChartPoint> points = dataPoints.Select((ChartPoint p) => new ChartPoint(MapX(p.X), MapY(p.Y))).ToList();
        List<AxisTick> xTicks = (from v in BuildTicks(minX, maxX, tickCount)
                                 select new AxisTick(MapX(v), FormatTick(v))).ToList();
        List<AxisTick> yTicks = (from v in BuildTicks(minY, maxY, tickCount)
                                 select new AxisTick(MapY(v), FormatTick(v))).ToList();
        return new ChartData(points, xTicks, yTicks);
        double MapX(double x)
        {
            return marginX + (x - minX) / xRange * plotWidth;
        }
        double MapY(double y)
        {
            return height - marginY - (y - minY) / yRange * plotHeight;
        }
    }

    private static List<double> BuildTicks(double min, double max, int tickCount)
    {
        if (tickCount < 2)
        {
            tickCount = 2;
        }
        double num = (max - min) / (double)(tickCount - 1);
        List<double> list = new List<double>();
        for (int i = 0; i < tickCount; i++)
        {
            list.Add(min + num * (double)i);
        }
        return list;
    }

    private static string FormatTick(double value)
    {
        return (Math.Abs(value) >= 100.0) ? value.ToString("0") : value.ToString("0.#");
    }
}
