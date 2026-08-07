using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the core-pinning advice. The premise this class is built on is that CPU usage is not
/// interrupt load - interrupts are far too short to register as CPU time, so the core servicing most
/// of them can read 0% busy and recommending it is exactly backwards. These tests hold it to that:
/// it must never present a guess from CPU usage as though it were a measurement.
/// </summary>
public sealed class CoreAffinityAdvisorTests
{
    private readonly ITestOutputHelper _output;

    public CoreAffinityAdvisorTests(ITestOutputHelper output) => _output = output;

    private static IReadOnlyList<CoreLoad> Loads(params (int Core, double Share)[] loads) =>
        loads.Select(l => new CoreLoad(l.Core, l.Share)).ToList();

    // --- No data --------------------------------------------------------------------------------

    [Fact]
    public void NoUsageDataSaysItIsStillCollecting()
    {
        Assert.Contains("Collecting", CoreAffinityAdvisor.Generate(
            Array.Empty<double>(), Array.Empty<int>()), StringComparison.Ordinal);
    }

    [Fact]
    public void AllZeroUsageSaysItIsStillCollecting()
    {
        Assert.Contains("Collecting", CoreAffinityAdvisor.Generate(
            new[] { 0.0, 0.0, 0.0, 0.0 }, Array.Empty<int>()), StringComparison.Ordinal);
    }

    // --- Interrupt data is preferred over CPU usage ----------------------------------------------

    [Fact]
    public void InterruptDataIsUsedEvenWhenEveryCoreReadsZeroCpu()
    {
        // The case the class exists for: all cores idle on CPU, but interrupts are concentrated.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 0.0, 0.0, 0.0, 0.0 },
            Array.Empty<int>(),
            Loads((0, 67.0), (1, 20.0), (2, 8.0), (3, 5.0)));

        Assert.Contains("core 3", text, StringComparison.Ordinal);
        Assert.Contains("DPC/ISR trace", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Collecting", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuietestCoreByInterruptsIsRecommendedNotTheQuietestByCpu()
    {
        // Core 0 is busiest on CPU but quietest on interrupts. Interrupts must win.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 90.0, 1.0, 1.0, 1.0 },
            Array.Empty<int>(),
            Loads((0, 2.0), (1, 40.0), (2, 30.0), (3, 28.0)));

        Assert.Contains("core 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHotPinnedCoreIsCalledOutAgainstTheQuietestAlternative()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0, 5.0, 5.0 },
            new[] { 0 },
            Loads((0, 67.0), (1, 20.0), (2, 8.0), (3, 5.0)));

        Assert.Contains("67%", text, StringComparison.Ordinal);
        Assert.Contains("Core 3 is far quieter", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuietPinnedCoreIsConfirmedAsAGoodChoice()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0, 5.0, 5.0 },
            new[] { 3 },
            Loads((0, 67.0), (1, 20.0), (2, 8.0), (3, 5.0)));

        Assert.Contains("quiet choice", text, StringComparison.Ordinal);
        Assert.Contains("5%", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(24.0, false)]  // below the hot threshold
    [InlineData(25.0, true)]   // at it - inclusive
    [InlineData(26.0, true)]
    public void TheHotCoreThresholdIsInclusive(double pinnedShare, bool shouldWarn)
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0 },
            new[] { 0 },
            Loads((0, pinnedShare), (1, 1.0)));

        Assert.Equal(shouldWarn, text.Contains("quieter", StringComparison.Ordinal));
    }

    /// <summary>
    /// The regression this pins. hottestPinned came from FirstOrDefault() over a struct sequence, so
    /// a pinned core absent from the trace data produced default(CoreLoad) - core 0, 0% - and the
    /// advice then read "servicing 0% of the system's interrupts, a quiet choice". That is a
    /// measurement-sounding claim manufactured from a default value: 0% because nothing landed there
    /// and 0% because the core is not in the data are entirely different facts, and only the first
    /// justifies calling it a good choice.
    /// </summary>
    [Fact]
    public void APinnedCoreMissingFromTheTraceIsNotReportedAsAQuietChoice()
    {
        // Core 7 is pinned, but the trace only recorded cores 0-3.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0 },
            new[] { 7 },
            Loads((0, 40.0), (1, 30.0), (2, 20.0), (3, 10.0)));

        _output.WriteLine(text);

        Assert.DoesNotContain("quiet choice", text, StringComparison.Ordinal);
        Assert.DoesNotContain("servicing 0%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiplePinnedCoresAreLabelledInThePlural()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0, 5.0, 5.0 },
            new[] { 2, 3 },
            Loads((0, 60.0), (1, 30.0), (2, 5.0), (3, 5.0)));

        Assert.Contains("Cores 2, 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASinglePinnedCoreIsLabelledInTheSingular()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 5.0, 5.0 },
            new[] { 1 },
            Loads((0, 60.0), (1, 5.0)));

        Assert.Contains("Core 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cores 1", text, StringComparison.Ordinal);
    }

    // --- CPU-usage fallback ----------------------------------------------------------------------

    [Fact]
    public void WithNoTraceAndTiedCoresItRefusesToNameOneAndAsksForATrace()
    {
        // Several equally idle cores carry no information about interrupt placement. Naming one would
        // be false precision.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 2.0, 2.0, 2.0, 2.0 }, Array.Empty<int>());

        Assert.Contains("can't tell them apart", text, StringComparison.Ordinal);
        Assert.Contains("DPC/ISR tab", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoTraceAndOneClearlyIdlerCoreItNamesThatCore()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 50.0, 60.0, 3.0, 55.0 }, Array.Empty<int>());

        Assert.Contains("Core 2", text, StringComparison.Ordinal);
        Assert.Contains("least busy", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AdviceFromCpuUsageAloneAlwaysAsksForATraceToConfirmIt()
    {
        // CPU usage cannot see interrupt load, so any pinned-core verdict from it must be provisional.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 50.0, 60.0, 3.0, 55.0 }, new[] { 2 });

        Assert.Contains("DPC/ISR tab", text, StringComparison.Ordinal);
        Assert.Contains("CPU usage alone won't show that", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClearlyBusierPinnedCoreIsFlaggedAgainstAnIdlerOne()
    {
        string text = CoreAffinityAdvisor.Generate(
            new[] { 80.0, 2.0, 70.0, 75.0 }, new[] { 0 });

        Assert.Contains("Core 1", text, StringComparison.Ordinal);
        Assert.Contains("consider switching", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedCoreWithinToleranceOfTheBestIsLeftAlone()
    {
        // 4 points apart, inside the 5-point "good enough" band - switching would be churn.
        string text = CoreAffinityAdvisor.Generate(
            new[] { 6.0, 2.0, 70.0, 75.0 }, new[] { 0 });

        Assert.DoesNotContain("consider switching", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An out-of-range pinned index contributed 0 to the pinned average, making the pinned set look
    /// idler than any real core could be and suppressing a switch recommendation that may be
    /// warranted. Out-of-range indices are not hypothetical: a saved profile from a machine with more
    /// cores, or a core-count change after a BIOS update, produces exactly this.
    /// </summary>
    [Fact]
    public void AnOutOfRangePinnedIndexDoesNotFabricateZeroUsage()
    {
        string withBogus = CoreAffinityAdvisor.Generate(
            new[] { 80.0, 2.0, 70.0, 75.0 }, new[] { 0, 99 });

        _output.WriteLine(withBogus);

        // Core 0 is at 80% and core 1 at 2%; the advice must still recommend switching rather than
        // being talked out of it by an invented 0%.
        Assert.Contains("consider switching", withBogus, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativePinnedIndicesAreIgnoredRatherThanCounted()
    {
        Exception? failure = Record.Exception(() => CoreAffinityAdvisor.Generate(
            new[] { 80.0, 2.0 }, new[] { -1, 0 }));

        Assert.Null(failure);
    }

    [Fact]
    public void EveryPinnedIndexBeingOutOfRangeIsHandled()
    {
        Exception? failure = Record.Exception(() => CoreAffinityAdvisor.Generate(
            new[] { 80.0, 2.0 }, new[] { 50, 99 }));

        Assert.Null(failure);
    }
}
