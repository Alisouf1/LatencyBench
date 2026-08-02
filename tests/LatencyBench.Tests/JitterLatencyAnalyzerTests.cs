using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.HidTesting;

namespace LatencyBench.Tests;

public class JitterLatencyAnalyzerTests
{
    /// <summary>One tick per millisecond keeps the arithmetic in the tests readable: an interval of
    /// N ticks is N milliseconds.</summary>
    private const double TicksPerMs = 1.0;

    private static List<long> TimestampsFromIntervals(params double[] intervalsMs)
    {
        var timestamps = new List<long> { 0 };
        long running = 0;
        foreach (double interval in intervalsMs)
        {
            running += (long)interval;
            timestamps.Add(running);
        }

        return timestamps;
    }

    [Fact]
    public void ReturnsNullWhenThereAreTooFewSamplesToFormIntervals()
    {
        Assert.Null(JitterLatencyAnalyzer.Analyze(new long[] { 0, 1 }, TicksPerMs));
    }

    [Fact]
    public void ReturnsNullForNonPositiveTickRate()
    {
        Assert.Null(JitterLatencyAnalyzer.Analyze(new long[] { 0, 1, 2, 3 }, 0.0));
    }

    [Fact]
    public void ReturnsNullWhenEveryTimestampIsIdentical()
    {
        // Every delta is zero, so no interval survives — a degenerate capture, not a 0 ms result.
        Assert.Null(JitterLatencyAnalyzer.Analyze(new long[] { 5, 5, 5, 5 }, TicksPerMs));
    }

    [Fact]
    public void DerivesPollingRateFromTheMedianInterval()
    {
        // A perfect 1000 Hz device: one report every millisecond.
        var result = JitterLatencyAnalyzer.Analyze(TimestampsFromIntervals(1, 1, 1, 1, 1, 1), TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value.MedianReportIntervalMs, precision: 6);
        Assert.Equal(1000.0, result.Value.PollingRateHz, precision: 6);
    }

    [Fact]
    public void PerfectlyRegularReportsHaveZeroJitter()
    {
        var result = JitterLatencyAnalyzer.Analyze(TimestampsFromIntervals(2, 2, 2, 2, 2, 2), TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value.JitterMs, precision: 9);
        Assert.Equal(0.0, result.Value.LateReportPercent, precision: 9);
    }

    [Fact]
    public void IdleGapsAreExcludedFromTheStatistics()
    {
        // The long gap is the user letting go of the mouse, not the device failing to report. Left in,
        // it would dominate the mean and collapse the effective polling rate.
        var withGap = TimestampsFromIntervals(1, 1, 1, 1, 5000, 1, 1, 1, 1);

        var result = JitterLatencyAnalyzer.Analyze(withGap, TicksPerMs);

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Value.ActiveIntervalsMs, interval => interval > 100);
        Assert.Equal(1000.0, result.Value.EffectivePollingRateHz, precision: 3);
    }

    [Fact]
    public void SubMedianBurstArtifactsAreExcludedFromJitter()
    {
        // Two reports delivered back to back after the driver coalesced them show up as a near-zero
        // interval. That is a delivery artifact, not the device polling faster, and it used to be fed
        // into the standard deviation and inflate the reported jitter.
        var steady = TimestampsFromIntervals(4, 4, 4, 4, 4, 4, 4, 4, 4, 4);
        var withBurst = TimestampsFromIntervals(4, 4, 4, 4, 0.2, 4, 4, 4, 4, 4);

        var steadyResult = JitterLatencyAnalyzer.Analyze(steady, TicksPerMs);
        var burstResult = JitterLatencyAnalyzer.Analyze(withBurst, TicksPerMs);

        Assert.NotNull(steadyResult);
        Assert.NotNull(burstResult);

        // The burst interval is below median * BurstArtifactMultiplier, so it must not appear at all.
        Assert.DoesNotContain(burstResult!.Value.ActiveIntervalsMs, interval => interval < 1.0);
        Assert.Equal(steadyResult!.Value.JitterMs, burstResult.Value.JitterMs, precision: 9);
    }

    [Fact]
    public void CountsIntervalsBeyondTheLateThresholdAsLateReports()
    {
        // Median is 2 ms, so the late threshold is 3 ms. Two of the eight retained intervals exceed it.
        var result = JitterLatencyAnalyzer.Analyze(
            TimestampsFromIntervals(2, 2, 2, 4, 2, 2, 5, 2),
            TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(8, result!.Value.ActiveIntervalsMs.Count);
        Assert.Equal(25.0, result.Value.LateReportPercent, precision: 6);
    }

    [Fact]
    public void JitterIsTheStandardDeviationOfTheRetainedIntervals()
    {
        var result = JitterLatencyAnalyzer.Analyze(TimestampsFromIntervals(4, 6, 4, 6, 4, 6), TicksPerMs);

        Assert.NotNull(result);

        var retained = result!.Value.ActiveIntervalsMs;
        double mean = retained.Average();
        double expected = Math.Sqrt(retained.Select(v => (v - mean) * (v - mean)).Average());

        Assert.Equal(expected, result.Value.JitterMs, precision: 9);
    }

    [Fact]
    public void SampleCountReportsTimestampsNotIntervals()
    {
        var timestamps = TimestampsFromIntervals(1, 1, 1, 1);

        var result = JitterLatencyAnalyzer.Analyze(timestamps, TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(timestamps.Count, result!.Value.SampleCount);
    }

    [Fact]
    public void NonMonotonicTimestampsAreSkippedRatherThanProducingNegativeIntervals()
    {
        // A backwards step would otherwise yield a negative interval and poison the mean.
        var timestamps = new long[] { 0, 10, 5, 20, 30, 40 };

        var result = JitterLatencyAnalyzer.Analyze(timestamps, TicksPerMs);

        Assert.NotNull(result);
        Assert.All(result!.Value.ActiveIntervalsMs, interval => Assert.True(interval > 0));
    }

    [Fact]
    public void FallsBackToTheUnfilteredSetWhenFilteringWouldDiscardEverything()
    {
        // Alternating 1 ms and 100 ms gives a median of 1 ms; the 100 ms entries are beyond the idle
        // cut and the 1 ms entries sit exactly at the median, so a stricter filter could empty the set.
        // The analyser must still produce a result rather than dividing by zero.
        var result = JitterLatencyAnalyzer.Analyze(
            TimestampsFromIntervals(100, 1, 100, 1, 100, 1),
            TicksPerMs);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Value.ActiveIntervalsMs);
        Assert.False(double.IsNaN(result.Value.JitterMs));
        Assert.False(double.IsNaN(result.Value.EffectivePollingRateHz));
    }

    [Fact]
    public void ScalesCorrectlyWhenTicksAreNotMilliseconds()
    {
        // Stopwatch.Frequency is usually 10 MHz, i.e. 10,000 ticks per millisecond.
        const double ticksPerMillisecond = 10_000.0;
        var timestamps = new List<long> { 0 };
        for (int i = 1; i <= 8; i++)
        {
            timestamps.Add(i * 20_000L); // 2 ms apart
        }

        var result = JitterLatencyAnalyzer.Analyze(timestamps, ticksPerMillisecond);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Value.MedianReportIntervalMs, precision: 6);
        Assert.Equal(500.0, result.Value.PollingRateHz, precision: 6);
    }
}
