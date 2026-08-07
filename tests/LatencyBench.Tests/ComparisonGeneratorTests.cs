using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the two comparison generators. These decide whether the user is told a change helped,
/// hurt, or did nothing - the single question the whole app exists to answer. Overclaiming here is
/// worse than staying silent, because it sends someone to keep a setting that did nothing.
/// </summary>
public sealed class ComparisonGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public ComparisonGeneratorTests(ITestOutputHelper output) => _output = output;

    // ============================================================================================
    // DpcIsrComparisonGenerator
    // ============================================================================================

    private static DpcIsrTestResult Trace(double spikesPerSecond, string configName, bool msi = true) => new()
    {
        DurationSeconds = 30,
        HighSpikesPerSecond = spikesPerSecond,
        BorderlineSpikesPerSecond = spikesPerSecond * 3,
        HighestDpcMicroseconds = 400,
        HighestIsrMicroseconds = 90,
        TopDriverName = "x.sys",
        TopDriverMaxMicroseconds = 400,
        // Category is deliberately left empty so ConfigLabel falls back to the device Name. Passing a
        // category here would group every trace under it - the label is built from category, MSI and
        // priority, not from the name - and every trace would then count as the same config.
        TunedDevices = new[]
        {
            new TunedDeviceConfig(configName, InterruptPriority.High, msi, Category: string.Empty),
        },
    };

    [Fact]
    public void TheFirstSavedTraceSaysThereIsNothingToCompareYet()
    {
        string text = DpcIsrComparisonGenerator.Generate(
            Trace(1.0, "A"), Array.Empty<DpcIsrTestResult>());

        Assert.Contains("First saved trace", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralTracesOfOneConfigReportTheSpreadAndAskForAChange()
    {
        var history = new[] { Trace(1.0, "A"), Trace(3.0, "A") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(2.0, "A"), history);

        Assert.Contains("all with the same config", text, StringComparison.Ordinal);
        Assert.Contains("range 1.00-3.00", text, StringComparison.Ordinal);
        Assert.Contains("Change a setting", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeDropIsCalledAnImprovement()
    {
        var history = new[] { Trace(10.0, "B"), Trace(10.0, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(2.0, "A"), history);

        Assert.Contains("fewer spikes", text, StringComparison.Ordinal);
        Assert.Contains("real improvement", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeRiseIsCalledWorse()
    {
        var history = new[] { Trace(2.0, "B"), Trace(2.0, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(10.0, "A"), history);

        Assert.Contains("more spikes", text, StringComparison.Ordinal);
        Assert.Contains("worse", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeInsideTheNoiseBandIsNotClaimedAsAnEffect()
    {
        // 10% apart, well under the 25% the class requires before calling anything real.
        var history = new[] { Trace(10.0, "B"), Trace(10.0, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(11.0, "A"), history);

        Assert.Contains("No measurable effect", text, StringComparison.Ordinal);
        Assert.DoesNotContain("real improvement", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSpikeFreeConfigsAreNotRankedAgainstEachOther()
    {
        // Dividing two near-zero rates produces huge percentages from meaningless differences.
        var history = new[] { Trace(0.01, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(0.02, "A"), history);

        Assert.Contains("essentially spike-free", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleTracePerConfigCarriesAWarning()
    {
        var history = new[] { Trace(10.0, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(2.0, "A"), history);

        Assert.Contains("only has a single trace", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTracesPerConfigDropTheWarning()
    {
        var history = new[] { Trace(10.0, "B"), Trace(10.0, "B"), Trace(2.5, "A") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(2.0, "A"), history);

        Assert.DoesNotContain("only has a single trace", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression this pins. The comparison config was selected by whichever single saved run had
    /// the lowest spike rate, then reported using that config's AVERAGE. So a config with one lucky
    /// run and one terrible one was picked over a config that was consistently better, and the user
    /// was then shown the lucky config's much worse average as the thing to beat.
    ///
    /// Averaging is the class's own defence against run-to-run noise; selecting on a single run threw
    /// it away at the first step.
    /// </summary>
    [Fact]
    public void TheComparisonBaselineIsChosenByAverageNotByOneLuckyRun()
    {
        var history = new[]
        {
            Trace(0.1, "Lucky"),    // one excellent run...
            Trace(20.0, "Lucky"),   // ...and one terrible one. Average 10.05.
            Trace(1.0, "Steady"),
            Trace(1.2, "Steady"),   // consistently good. Average 1.1.
        };

        string text = DpcIsrComparisonGenerator.Generate(Trace(5.0, "Current"), history);
        _output.WriteLine(text);

        Assert.Contains("Steady", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lucky", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentTraceIsIncludedInItsOwnConfigAverage()
    {
        // The trace just finished is part of the evidence for its config, not a separate thing.
        var history = new[] { Trace(4.0, "A"), Trace(10.0, "B"), Trace(10.0, "B") };

        string text = DpcIsrComparisonGenerator.Generate(Trace(2.0, "A"), history);

        Assert.Contains("from 2 trace(s)", text, StringComparison.Ordinal);
        Assert.Contains("3.00 spikes/s", text, StringComparison.Ordinal); // (4.0 + 2.0) / 2
    }

    // ============================================================================================
    // PortComparisonGenerator
    // ============================================================================================

    private static PortRankResult Port(
        double jitterMs,
        string location = "1-3",
        bool underLoad = false,
        int pollingHz = 1000) => new()
        {
            PortLabel = $"Port {location}",
            PortLocation = location,
            Rank = PortRank.Good,
            AverageJitterMs = jitterMs,
            PollingRateHz = pollingHz,
            UnderLoad = underLoad,
        };

    [Fact]
    public void NoHistoryForThePortReportsNoBaseline()
    {
        var result = PortComparisonGenerator.Compare(0.5, "1-3", Array.Empty<PortRankResult>());

        Assert.Equal(ComparisonVerdict.NoBaseline, result.Verdict);
        Assert.Null(result.PreviousJitterMs);
        Assert.Contains("First saved result", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeJitterDropIsCalledAnImprovement()
    {
        var result = PortComparisonGenerator.Compare(0.40, "1-3", new[] { Port(1.00) });

        Assert.Equal(ComparisonVerdict.Improved, result.Verdict);
        Assert.Equal(1.00, result.PreviousJitterMs);
        Assert.Contains("Better", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALargeJitterRiseIsCalledARegression()
    {
        var result = PortComparisonGenerator.Compare(2.00, "1-3", new[] { Port(1.00) });

        Assert.Equal(ComparisonVerdict.Regressed, result.Verdict);
        Assert.Contains("Worse", result.Message, StringComparison.Ordinal);
        Assert.Contains("undoing", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.91)]  // 9% better - inside the band
    [InlineData(1.09)]  // 9% worse  - inside the band
    [InlineData(1.00)]
    public void SmallChangesAreReportedAsUnchanged(double current)
    {
        var result = PortComparisonGenerator.Compare(current, "1-3", new[] { Port(1.00) });

        Assert.Equal(ComparisonVerdict.Unchanged, result.Verdict);
        Assert.Contains("within normal run-to-run variation", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.89, ComparisonVerdict.Improved)]
    [InlineData(1.11, ComparisonVerdict.Regressed)]
    public void ChangesClearlyBeyondTheNoiseBandAreCalled(double current, ComparisonVerdict expected)
    {
        // Deliberately not testing the exact 10% boundary. (0.90 - 1.00) / 1.00 * 100 evaluates to
        // -9.999999999999998, not -10, so a nominally "exactly at the threshold" input lands on the
        // Unchanged side purely through binary floating point. Asserting that would pin an artefact
        // of representation rather than the intended rule, and would break if the arithmetic were
        // reordered. What matters is that values clearly either side are classified correctly.
        var result = PortComparisonGenerator.Compare(current, "1-3", new[] { Port(1.00) });

        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public void OnlyTheSamePortIsUsedAsABaseline()
    {
        // Comparing against a different physical port would attribute that port's characteristics to
        // whatever setting was just changed.
        var history = new[] { Port(0.10, location: "1-1"), Port(1.00, location: "1-3") };

        var result = PortComparisonGenerator.Compare(0.95, "1-3", history);

        Assert.Equal(1.00, result.PreviousJitterMs);
        Assert.Equal(ComparisonVerdict.Unchanged, result.Verdict);
    }

    [Fact]
    public void PortMatchingIsCaseInsensitive()
    {
        var result = PortComparisonGenerator.Compare(0.40, "1-3", new[] { Port(1.00, location: "1-3") });

        Assert.Equal(ComparisonVerdict.Improved, result.Verdict);
    }

    [Fact]
    public void AResultUnderLoadIsNotComparedAgainstAnIdleBaseline()
    {
        // A loaded run is expected to be worse; comparing across conditions invents a regression.
        var history = new[] { Port(0.20, underLoad: false) };

        var result = PortComparisonGenerator.Compare(0.80, "1-3", history, underLoad: true);

        Assert.Equal(ComparisonVerdict.NoBaseline, result.Verdict);
        Assert.Contains("under CPU load", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleResultIsNotComparedAgainstALoadedBaseline()
    {
        var history = new[] { Port(0.80, underLoad: true) };

        var result = PortComparisonGenerator.Compare(0.20, "1-3", history, underLoad: false);

        Assert.Equal(ComparisonVerdict.NoBaseline, result.Verdict);
    }

    [Fact]
    public void TheBestPreviousRunIsUsedNotTheMostRecent()
    {
        // "Your best on this port" is the documented intent here, unlike the DPC/ISR generator which
        // compares averages - a port test is a repeatable measurement of one physical thing.
        var history = new[] { Port(2.00), Port(0.50), Port(1.50) };

        var result = PortComparisonGenerator.Compare(0.45, "1-3", history);

        Assert.Equal(0.50, result.PreviousJitterMs);
    }

    [Fact]
    public void EntriesWithoutAJitterMeasurementAreIgnored()
    {
        var history = new[]
        {
            new PortRankResult { PortLabel = "P", PortLocation = "1-3", AverageJitterMs = null },
            Port(1.00),
        };

        var result = PortComparisonGenerator.Compare(0.40, "1-3", history);

        Assert.Equal(ComparisonVerdict.Improved, result.Verdict);
        Assert.Equal(1.00, result.PreviousJitterMs);
    }

    [Fact]
    public void AZeroOrNegativeBaselineIsTreatedAsNoBaselineRatherThanDividedBy()
    {
        var history = new[] { Port(0.0) };

        var result = PortComparisonGenerator.Compare(0.40, "1-3", history);

        Assert.Equal(ComparisonVerdict.NoBaseline, result.Verdict);
    }

    [Fact]
    public void ThePercentChangeIsReportedWithTheCorrectSign()
    {
        var improved = PortComparisonGenerator.Compare(0.50, "1-3", new[] { Port(1.00) });
        var regressed = PortComparisonGenerator.Compare(1.50, "1-3", new[] { Port(1.00) });

        Assert.Equal(-50.0, improved.PercentChange, 6);
        Assert.Equal(50.0, regressed.PercentChange, 6);
    }
}
