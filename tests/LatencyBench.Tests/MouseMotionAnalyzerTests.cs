using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.MouseTesting.Models;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the mouse motion analysis, which had no tests at all despite producing the figures the
/// Mouse test tab presents as measurements: path length, peak speed, spike indices, the angle-snap
/// score and the speed/gain bands. A defect here does not crash anything - it makes the app state a
/// wrong number confidently, which is worse.
/// </summary>
public sealed class MouseMotionAnalyzerTests
{
    private const double TicksPerMs = 10_000.0;

    private readonly ITestOutputHelper _output;

    public MouseMotionAnalyzerTests(ITestOutputHelper output) => _output = output;

    /// <summary>Samples spaced at a fixed interval, each carrying the given delta.</summary>
    private static List<MouseSample> Samples(double intervalMs, params (int Dx, int Dy)[] deltas)
    {
        var list = new List<MouseSample>(deltas.Length);
        long ticks = 0;
        foreach ((int dx, int dy) in deltas)
        {
            list.Add(new MouseSample(ticks, dx, dy, 0));
            ticks += (long)(intervalMs * TicksPerMs);
        }

        return list;
    }

    private static List<MouseSample> Straight(int count, int dx, int dy, double intervalMs = 1.0) =>
        Samples(intervalMs, Enumerable.Repeat((dx, dy), count).ToArray());

    // --- Guard conditions ------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerThanThreeSamplesYieldsNoResult(int count)
    {
        Assert.Null(MouseMotionAnalyzer.Analyze(Straight(count, 1, 0), TicksPerMs));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ANonPositiveTickRateYieldsNoResult(double ticksPerMs)
    {
        Assert.Null(MouseMotionAnalyzer.Analyze(Straight(10, 1, 0), ticksPerMs));
    }

    [Fact]
    public void AnEmptyListIsHandledWithoutThrowing()
    {
        Assert.Null(MouseMotionAnalyzer.Analyze(Array.Empty<MouseSample>(), TicksPerMs));
    }

    // --- Geometry --------------------------------------------------------------------------------

    [Fact]
    public void StraightLineMotionHasPathLengthEqualToNetDisplacement()
    {
        // 10 reports of (3,4) - each of magnitude 5 - in one direction. Path and displacement must
        // agree, because nothing doubled back.
        var result = MouseMotionAnalyzer.Analyze(Straight(10, 3, 4), TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(50.0, result!.Value.TotalPathCounts, 6);
        Assert.Equal(50.0, result.Value.NetDisplacementCounts, 6);
    }

    [Fact]
    public void MotionThatReturnsToTheOriginHasPathLengthButNoDisplacement()
    {
        // Right 10, then left 10. This is the case that separates the two figures; if the analyzer
        // conflated them, a user shaking the mouse in place would read as having travelled nowhere.
        var deltas = Enumerable.Repeat((10, 0), 5).Concat(Enumerable.Repeat((-10, 0), 5)).ToArray();

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(100.0, result!.Value.TotalPathCounts, 6);
        Assert.Equal(0.0, result.Value.NetDisplacementCounts, 6);
    }

    [Fact]
    public void DurationIsMeasuredFirstSampleToLast()
    {
        // 10 samples at 2 ms apart spans 18 ms, not 20: the first sample is time zero.
        var result = MouseMotionAnalyzer.Analyze(Straight(10, 1, 0, intervalMs: 2.0), TicksPerMs);

        Assert.Equal(18.0, result!.Value.DurationMs, 6);
        Assert.Equal(10, result.Value.SampleCount);
    }

    [Fact]
    public void PeakSpeedIsCountsPerMillisecond()
    {
        // Constant (6,8) - magnitude 10 - every 2 ms is 5 counts/ms.
        var result = MouseMotionAnalyzer.Analyze(Straight(8, 6, 8, intervalMs: 2.0), TicksPerMs);

        Assert.Equal(5.0, result!.Value.PeakSpeedCountsPerMs, 6);
    }

    [Fact]
    public void ZeroMovementProducesNoSpeedAndNoBands()
    {
        var result = MouseMotionAnalyzer.Analyze(Straight(10, 0, 0), TicksPerMs);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value.PeakSpeedCountsPerMs, 6);
        Assert.Equal(0.0, result.Value.TotalPathCounts, 6);
        Assert.Empty(result.Value.SpeedGainBands);
        Assert.Empty(result.Value.SpikeSampleIndices);
    }

    // --- Spike detection -------------------------------------------------------------------------

    [Fact]
    public void SteadyMotionProducesNoSpikes()
    {
        var result = MouseMotionAnalyzer.Analyze(Straight(30, 5, 0), TicksPerMs);

        Assert.Empty(result!.Value.SpikeSampleIndices);
    }

    [Fact]
    public void ASingleOutlierIsReportedAtItsOwnIndex()
    {
        // 20 steady reports with one 20x jump at index 10. The threshold is 5x the median speed.
        var deltas = Enumerable.Repeat((5, 0), 20).ToArray();
        deltas[10] = (100, 0);

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        Assert.Equal(new[] { 10 }, result!.Value.SpikeSampleIndices);
    }

    [Fact]
    public void AnOutlierExactlyAtTheThresholdIsNotCounted()
    {
        // The rule is strictly greater than 5x the median, so 5x itself must not register. Pins the
        // boundary rather than leaving it to drift.
        var deltas = Enumerable.Repeat((10, 0), 21).ToArray();
        deltas[10] = (50, 0);

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        Assert.Empty(result!.Value.SpikeSampleIndices);
    }

    [Fact]
    public void TheFirstSampleIsNeverASpike()
    {
        // Index 0 has no predecessor, so it has no speed. It must not be reported as a spike on the
        // strength of an undefined value.
        var deltas = Enumerable.Repeat((5, 0), 20).ToArray();
        deltas[0] = (5000, 0);

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        Assert.DoesNotContain(0, result!.Value.SpikeSampleIndices);
    }

    // --- Speed / gain bands ----------------------------------------------------------------------

    [Fact]
    public void ConstantSpeedFallsEntirelyInTheTopBand()
    {
        // Every sample is at peak, so the ratio is 1.0 and only the 80-100% band is populated.
        var result = MouseMotionAnalyzer.Analyze(Straight(20, 10, 0), TicksPerMs);

        SpeedGainBand band = Assert.Single(result!.Value.SpeedGainBands);
        Assert.Equal("80-100%", band.Label);
        Assert.Equal(10.0, band.AverageCountsPerReport, 6);
        Assert.Equal(19, band.SampleCount); // index 0 has no speed
    }

    [Fact]
    public void BandSampleCountsSumToTheNumberOfSamplesWithASpeed()
    {
        var random = new Random(20260805);
        var deltas = Enumerable.Range(0, 200).Select(_ => (random.Next(1, 40), random.Next(1, 40))).ToArray();

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        int banded = result!.Value.SpeedGainBands.Sum(b => b.SampleCount);
        Assert.Equal(199, banded); // 200 samples, the first has no speed
    }

    [Fact]
    public void BandLabelsAreOrderedSlowestToFastest()
    {
        var random = new Random(7);
        var deltas = Enumerable.Range(0, 300).Select(i => (random.Next(1, 50), 0)).ToArray();

        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);

        string[] labels = result!.Value.SpeedGainBands.Select(b => b.Label).ToArray();
        string[] canonical = { "0-20%", "20-40%", "40-60%", "60-80%", "80-100%" };
        Assert.Equal(canonical.Where(labels.Contains).ToArray(), labels);
    }

    // --- Angle snap ------------------------------------------------------------------------------

    [Fact]
    public void PureHorizontalMotionScoresAboveOneBecauseItSitsOnACardinalAngle()
    {
        // Score is the fraction of travel on a snap bin divided by the fraction expected from an
        // even spread (8 bins of 5 degrees out of 360 = 1/9). All-cardinal motion therefore scores 9.
        var result = MouseMotionAnalyzer.Analyze(Straight(40, 10, 0), TicksPerMs);

        Assert.Equal(9.0, result!.Value.AngleSnapScore, 3);
    }

    [Fact]
    public void MotionWellAwayFromAnyCardinalAngleScoresZero()
    {
        // atan2(10,25) is about 21.8 degrees - not within any 5-degree snap bin.
        var result = MouseMotionAnalyzer.Analyze(Straight(40, 25, 10), TicksPerMs);

        Assert.Equal(0.0, result!.Value.AngleSnapScore, 6);
    }

    [Fact]
    public void MotionTooSmallToHaveADirectionIsIgnoredRatherThanCountedAsSnapped()
    {
        // Sub-2-count windows have no meaningful angle; counting them would let sensor noise
        // masquerade as snapping.
        var result = MouseMotionAnalyzer.Analyze(Straight(40, 0, 0), TicksPerMs);

        Assert.Equal(0.0, result!.Value.AngleSnapScore, 6);
    }

    /// <summary>
    /// The regression test for the binning defect. Detection used fixed 5-degree bins indexed by
    /// floor(angle/5), which put every cardinal angle on a bin BOUNDARY: bin 9 spans [45,50), so 46
    /// degrees counted as snapped to 45 while 44 degrees did not. Measured before the fix: 46 scored
    /// 9.000 and 44 scored 0.000. Real snapping scatters either side of its target, so about half of
    /// it went unrecorded and the score read low.
    /// </summary>
    [Fact]
    public void SnapDetectionIsSymmetricAroundACardinalAngle()
    {
        var justAbove = MouseMotionAnalyzer.Analyze(Straight(40, 100, 104), TicksPerMs);  // ~46.1 deg
        var justBelow = MouseMotionAnalyzer.Analyze(Straight(40, 104, 100), TicksPerMs);  // ~43.9 deg

        _output.WriteLine($"above 45 -> score {justAbove!.Value.AngleSnapScore:0.000}");
        _output.WriteLine($"below 45 -> score {justBelow!.Value.AngleSnapScore:0.000}");

        Assert.Equal(justAbove.Value.AngleSnapScore, justBelow.Value.AngleSnapScore, 6);
        Assert.True(justAbove.Value.AngleSnapScore > 0.0, "both sit within tolerance of 45 degrees");
    }

    [Fact]
    public void MotionJustOutsideTheToleranceIsNotCountedOnEitherSide()
    {
        // Tolerance is 2.5 degrees. atan2(115,100) is 48.99 degrees - 3.99 off 45, so outside it.
        var above = MouseMotionAnalyzer.Analyze(Straight(40, 100, 115), TicksPerMs);   // ~48.99 deg
        var below = MouseMotionAnalyzer.Analyze(Straight(40, 115, 100), TicksPerMs);   // ~41.01 deg

        Assert.Equal(0.0, above!.Value.AngleSnapScore, 6);
        Assert.Equal(0.0, below!.Value.AngleSnapScore, 6);
    }

    [Fact]
    public void AnglesNearZeroWrapCorrectlyRatherThanFallingOutsideEveryTarget()
    {
        // 359 degrees is 1 degree from the 0/360 target. Rounding maps it to 360, so wraparound must
        // need no special case - if it did, motion just below horizontal would score zero.
        var result = MouseMotionAnalyzer.Analyze(Straight(40, 1000, -17), TicksPerMs);  // ~359.03 deg

        Assert.True(result!.Value.AngleSnapScore > 0.0,
            $"motion at ~359 degrees should count as snapped to 0; scored {result.Value.AngleSnapScore}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(225)]
    [InlineData(270)]
    [InlineData(315)]
    public void EveryCardinalAndDiagonalDirectionScoresTheMaximum(int degrees)
    {
        // All eight targets must behave identically. Any that did not would mean the tolerance test
        // has an edge case left in it.
        double radians = degrees * Math.PI / 180.0;
        int dx = (int)Math.Round(Math.Cos(radians) * 1000.0);
        int dy = (int)Math.Round(Math.Sin(radians) * 1000.0);

        var result = MouseMotionAnalyzer.Analyze(Straight(40, dx, dy), TicksPerMs);

        Assert.Equal(9.0, result!.Value.AngleSnapScore, 3);
    }

    // --- Robustness ------------------------------------------------------------------------------

    [Fact]
    public void IdenticalTimestampsDoNotProduceInfinityOrNaN()
    {
        // A burst delivered with the same timestamp would divide by a zero interval.
        var samples = new List<MouseSample>
        {
            new(0, 5, 5, 0),
            new(0, 5, 5, 0),
            new(0, 5, 5, 0),
            new(10_000, 5, 5, 0),
        };

        var result = MouseMotionAnalyzer.Analyze(samples, TicksPerMs);

        Assert.NotNull(result);
        Assert.False(double.IsNaN(result!.Value.PeakSpeedCountsPerMs));
        Assert.False(double.IsInfinity(result.Value.PeakSpeedCountsPerMs));
        Assert.All(result.Value.SpeedGainBands, b =>
        {
            Assert.False(double.IsNaN(b.AverageCountsPerReport));
            Assert.False(double.IsInfinity(b.AverageCountsPerReport));
        });
    }

    [Fact]
    public void ExtremeDeltasDoNotOverflowToNaN()
    {
        // int.MaxValue deltas squared exceed double precision comfortably but must stay finite.
        var samples = new List<MouseSample>
        {
            new(0, int.MaxValue, int.MaxValue, 0),
            new(10_000, int.MinValue, int.MinValue, 0),
            new(20_000, int.MaxValue, int.MaxValue, 0),
        };

        var result = MouseMotionAnalyzer.Analyze(samples, TicksPerMs);

        Assert.NotNull(result);
        Assert.False(double.IsNaN(result!.Value.TotalPathCounts));
        Assert.False(double.IsInfinity(result.Value.TotalPathCounts));
        Assert.False(double.IsNaN(result.Value.NetDisplacementCounts));
    }

    [Fact]
    public void NegativeTimeDeltasDoNotProduceNegativeSpeeds()
    {
        // Out-of-order timestamps are not expected, but a clock adjustment mid-capture could produce
        // them, and a negative speed would corrupt the median and therefore spike detection.
        var samples = new List<MouseSample>
        {
            new(30_000, 5, 0, 0),
            new(20_000, 5, 0, 0),
            new(10_000, 5, 0, 0),
            new(40_000, 5, 0, 0),
        };

        var result = MouseMotionAnalyzer.Analyze(samples, TicksPerMs);

        Assert.NotNull(result);
        Assert.True(result!.Value.PeakSpeedCountsPerMs >= 0.0,
            $"peak speed was negative: {result.Value.PeakSpeedCountsPerMs}");
    }

    [Fact]
    public void ALargeCaptureCompletesQuickly()
    {
        // Real captures run to tens of thousands of samples; the analysis runs on the UI thread when
        // a test finishes, so it must not be superlinear.
        var random = new Random(99);
        var deltas = Enumerable.Range(0, 100_000).Select(_ => (random.Next(-50, 50), random.Next(-50, 50))).ToArray();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = MouseMotionAnalyzer.Analyze(Samples(1.0, deltas), TicksPerMs);
        stopwatch.Stop();

        Assert.NotNull(result);
        _output.WriteLine($"100,000 samples analysed in {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"100k samples took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }
}
