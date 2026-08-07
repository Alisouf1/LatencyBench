using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the late-report correlation, which decides whether interrupt spikes actually explain late
/// HID reports and which driver to name. Getting this wrong points the user at an innocent driver,
/// which is worse than declining to answer - and declining is a first-class outcome here.
/// </summary>
public sealed class LateReportCorrelatorTests
{
    private static readonly DateTime Origin = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper _output;

    public LateReportCorrelatorTests(ITestOutputHelper output) => _output = output;

    /// <summary>Consecutive 1 ms windows; the indices in <paramref name="lateIndices"/> are marked late.</summary>
    private static List<ReportWindow> Windows(int count, params int[] lateIndices)
    {
        var late = new HashSet<int>(lateIndices);
        return Enumerable.Range(0, count)
            .Select(i => new ReportWindow(
                Origin.AddMilliseconds(i),
                Origin.AddMilliseconds(i + 1).AddTicks(-1),
                late.Contains(i)))
            .ToList();
    }

    /// <summary>A spike landing in the middle of window <paramref name="windowIndex"/>.</summary>
    private static SpikeEvent SpikeIn(int windowIndex, string driver, double us = 800) =>
        new(Origin.AddMilliseconds(windowIndex).AddTicks(5000), us, driver);

    // --- Nothing to explain ----------------------------------------------------------------------

    [Fact]
    public void NoLateReportsSaysThereIsNothingToExplain()
    {
        var result = LateReportCorrelator.Correlate(Windows(10), Array.Empty<SpikeEvent>());

        Assert.Equal(0, result.LateCount);
        Assert.Equal(10, result.OnTimeCount);
        Assert.Null(result.TopDriver);
        Assert.Contains("nothing to explain", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWindowsAtAllIsHandled()
    {
        var result = LateReportCorrelator.Correlate(
            Array.Empty<ReportWindow>(), Array.Empty<SpikeEvent>());

        Assert.Equal(0, result.LateCount);
        Assert.Null(result.TopDriver);
    }

    [Fact]
    public void LateReportsWithNoSpikesAtAllBlameTheDeviceNotADriver()
    {
        var result = LateReportCorrelator.Correlate(
            Windows(10, 1, 3, 5), Array.Empty<SpikeEvent>());

        Assert.Equal(3, result.LateCount);
        Assert.Equal(0, result.LateWithSpike);
        Assert.Null(result.TopDriver);
        Assert.Contains("no driver is clearly responsible", result.Message, StringComparison.Ordinal);
        Assert.Contains("device, cable, or hub", result.Message, StringComparison.Ordinal);
    }

    // --- The lift rule ---------------------------------------------------------------------------

    [Fact]
    public void ADriverSpikingInEveryWindowIsNotBlamed()
    {
        // The failure this class exists to prevent. A driver that fires constantly appears in every
        // late window - and every on-time one too - so it explains nothing.
        var windows = Windows(20, 1, 5, 9, 13);
        var spikes = Enumerable.Range(0, 20).Select(i => SpikeIn(i, "chatty.sys")).ToList();

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Null(result.TopDriver);
        Assert.Contains("about as often", result.Message, StringComparison.Ordinal);
        Assert.Contains("aren't what's making reports late", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriverSpikingOnlyDuringLateReportsIsBlamed()
    {
        var windows = Windows(20, 2, 6, 10, 14);
        var spikes = new[] { 2, 6, 10, 14 }.Select(i => SpikeIn(i, "culprit.sys")).ToList();

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal("culprit.sys", result.TopDriver);
        Assert.Equal(4, result.LateWithSpike);
        Assert.Equal(0, result.OnTimeWithSpike);
        Assert.Contains("never during on-time reports", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiftBelowTheThresholdIsNotBlamed()
    {
        // 50% of late windows hit, 40% of on-time - a lift of 1.25, well under the 2.0 required.
        var windows = Windows(20, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var spikes = new List<SpikeEvent>();
        spikes.AddRange(new[] { 0, 1, 2, 3, 4 }.Select(i => SpikeIn(i, "ambient.sys")));       // 5/10 late
        spikes.AddRange(new[] { 10, 11, 12, 13 }.Select(i => SpikeIn(i, "ambient.sys")));      // 4/10 on-time

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Null(result.TopDriver);
        Assert.Contains("about as often", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiftAtExactlyTheThresholdIsBlamed()
    {
        // 60% of late windows, 30% of on-time - a lift of exactly 2.0, which the rule accepts.
        var windows = Windows(20, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var spikes = new List<SpikeEvent>();
        spikes.AddRange(new[] { 0, 1, 2, 3, 4, 5 }.Select(i => SpikeIn(i, "suspect.sys")));    // 6/10
        spikes.AddRange(new[] { 10, 11, 12 }.Select(i => SpikeIn(i, "suspect.sys")));          // 3/10

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal("suspect.sys", result.TopDriver);
        Assert.Contains("2x more often", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainingTooFewLateReportsIsNotBlamed()
    {
        // Only 10% of late windows had a spike. Even at infinite lift, naming a driver would claim to
        // have explained the problem while leaving 90% of it unaccounted for.
        var windows = Windows(30, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var spikes = new[] { SpikeIn(0, "rare.sys") };

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Null(result.TopDriver);
        Assert.Contains("no driver is clearly responsible", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLateShareThresholdIsInclusive()
    {
        // Exactly 20% of late windows hit, and none on-time.
        var windows = Windows(30, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        var spikes = new[] { 0, 1 }.Select(i => SpikeIn(i, "edge.sys")).ToList();

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal("edge.sys", result.TopDriver);
    }

    // --- Window matching -------------------------------------------------------------------------

    [Fact]
    public void SpikesOutsideEveryWindowAreIgnored()
    {
        // Idle pauses between captures produce timestamps that belong to no report window.
        var windows = Windows(10, 1, 2, 3);
        var spikes = new[]
        {
            new SpikeEvent(Origin.AddHours(-1), 900, "before.sys"),
            new SpikeEvent(Origin.AddHours(1), 900, "after.sys"),
        };

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal(0, result.LateWithSpike);
        Assert.Null(result.TopDriver);
    }

    [Fact]
    public void ASpikeExactlyOnAWindowBoundaryIsMatched()
    {
        var windows = Windows(10, 4);
        var atStart = new[] { new SpikeEvent(Origin.AddMilliseconds(4), 900, "boundary.sys") };

        var result = LateReportCorrelator.Correlate(windows, atStart);

        Assert.Equal(1, result.LateWithSpike);
    }

    [Fact]
    public void UnorderedWindowsAreSortedBeforeSearching()
    {
        // The binary search requires sorted input; the caller is not required to provide it.
        var windows = Windows(10, 3);
        var shuffled = windows.AsEnumerable().Reverse().ToList();

        var result = LateReportCorrelator.Correlate(shuffled, new[] { SpikeIn(3, "found.sys") });

        Assert.Equal(1, result.LateWithSpike);
        Assert.Equal(1, result.LateCount);
    }

    [Fact]
    public void MultipleSpikesInOneWindowCountAsOneAffectedWindow()
    {
        // Shares are per window, not per event; otherwise a burst inside a single window could push
        // the share above 100%.
        var windows = Windows(20, 0, 1);
        var spikes = Enumerable.Range(0, 50).Select(_ => SpikeIn(0, "burst.sys")).ToList();

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal(1, result.LateWithSpike);
        Assert.True(result.LateWithSpike <= result.LateCount,
            "affected windows can never exceed total windows");
    }

    // --- Driver attribution ----------------------------------------------------------------------

    /// <summary>
    /// Attribution must use how many late WINDOWS a driver appears in, not how many raw events it
    /// fired. The lift calculation already works in windows precisely so that one chatty driver
    /// cannot dominate; counting events for the blame step would reintroduce that bias at the last
    /// moment - a driver firing 500 times inside a single late window would outrank one that is
    /// present in every late window.
    /// </summary>
    [Fact]
    public void AttributionCountsLateWindowsNotRawEvents()
    {
        var windows = Windows(20, 0, 1, 2, 3);

        var spikes = new List<SpikeEvent>();
        // A burst confined to ONE late window.
        spikes.AddRange(Enumerable.Range(0, 500).Select(_ => SpikeIn(0, "burst.sys")));
        // Present in THREE different late windows - the better explanation.
        spikes.Add(SpikeIn(1, "persistent.sys"));
        spikes.Add(SpikeIn(2, "persistent.sys"));
        spikes.Add(SpikeIn(3, "persistent.sys"));

        var result = LateReportCorrelator.Correlate(windows, spikes);

        _output.WriteLine($"top driver: {result.TopDriver}");
        Assert.Equal("persistent.sys", result.TopDriver);
    }

    [Fact]
    public void OnlyDriversSeenInLateWindowsAreCandidates()
    {
        var windows = Windows(20, 5);
        var spikes = new List<SpikeEvent>
        {
            SpikeIn(5, "late.sys"),
            SpikeIn(10, "ontime.sys"),
            SpikeIn(11, "ontime.sys"),
            SpikeIn(12, "ontime.sys"),
        };

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal("late.sys", result.TopDriver);
    }

    // --- Counts ----------------------------------------------------------------------------------

    [Fact]
    public void TheReportedCountsMatchTheInput()
    {
        var windows = Windows(25, 1, 4, 7, 11, 16);
        var spikes = new[] { SpikeIn(1, "a.sys"), SpikeIn(4, "a.sys"), SpikeIn(20, "b.sys") };

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal(5, result.LateCount);
        Assert.Equal(20, result.OnTimeCount);
        Assert.Equal(2, result.LateWithSpike);
        Assert.Equal(1, result.OnTimeWithSpike);
    }

    [Fact]
    public void EveryWindowLateIsHandledWithoutDividingByZero()
    {
        // No on-time windows at all means the on-time share is zero and lift is infinite.
        var windows = Windows(5, 0, 1, 2, 3, 4);
        var spikes = new[] { 0, 1, 2 }.Select(i => SpikeIn(i, "everything.sys")).ToList();

        var result = LateReportCorrelator.Correlate(windows, spikes);

        Assert.Equal(0, result.OnTimeCount);
        Assert.Equal("everything.sys", result.TopDriver);
        Assert.DoesNotContain("NaN", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeCorrelationCompletesQuickly()
    {
        // Real runs pair tens of thousands of report windows with tens of thousands of spikes; the
        // window lookup must stay logarithmic rather than scanning.
        var windows = Windows(50_000, Enumerable.Range(0, 50_000).Where(i => i % 10 == 0).ToArray());
        var random = new Random(4);
        var spikes = Enumerable.Range(0, 50_000)
            .Select(_ => SpikeIn(random.Next(0, 50_000), "x.sys"))
            .ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = LateReportCorrelator.Correlate(windows, spikes);
        stopwatch.Stop();

        _output.WriteLine($"50k windows x 50k spikes in {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(5_000, result.LateCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"correlation took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }
}
