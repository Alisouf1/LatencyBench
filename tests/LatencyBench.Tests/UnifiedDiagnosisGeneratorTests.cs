using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the Action Plan orchestrator - the thing that reads every tab's data and produces the
/// ranked findings on the Dashboard. The individual rules reuse thresholds already tested elsewhere;
/// what is tested here is the contract around them: that nothing is invented when there is no data,
/// that the list is capped and ranked, and that no combination of inputs can make it throw on the UI
/// thread.
/// </summary>
public sealed class UnifiedDiagnosisGeneratorTests
{
    private readonly ITestOutputHelper _output;

    public UnifiedDiagnosisGeneratorTests(ITestOutputHelper output) => _output = output;

    private static PortRankResult Port(
        double? jitterMs,
        string location = "1-3",
        int pollingHz = 1000,
        string? controllerInstanceId = null) => new()
        {
            PortLabel = $"Port {location}",
            PortLocation = location,
            Rank = PortRank.Good,
            AverageJitterMs = jitterMs,
            PollingRateHz = pollingHz,
            HostControllerInstanceId = controllerInstanceId,
        };

    private static DpcIsrTestResult Trace(
        double spikesPerSecond,
        string topDriver = "nvlddmkm.sys",
        DateTime? savedAt = null) => new()
    {
        DurationSeconds = 30,
        HighSpikesPerSecond = spikesPerSecond,
        BorderlineSpikesPerSecond = spikesPerSecond * 3,
        HighestDpcMicroseconds = 600,
        HighestIsrMicroseconds = 90,
        TopDriverName = topDriver,
        TopDriverMaxMicroseconds = 600,
        SavedAt = savedAt ?? DateTime.Now,
    };

    private static ControllerSignal Controller(
        string label = "USB Controller 1",
        string instanceId = "PCI\\VEN_1022&DEV_43EE\\1",
        InterruptAffinityPolicy policy = InterruptAffinityPolicy.MachineDefault,
        int[]? pinnedCores = null,
        double? pinnedShare = null,
        bool latencySensitive = true,
        string? contention = null) =>
        new(label, 1, instanceId, contention, policy, pinnedCores ?? Array.Empty<int>(), pinnedShare, latencySensitive);

    private static IReadOnlyList<DiagnosisFinding> Generate(
        IReadOnlyList<PortRankResult>? ports = null,
        IReadOnlyList<DpcIsrTestResult>? traces = null,
        IReadOnlyList<ControllerSignal>? controllers = null,
        IReadOnlyList<CoreLoad>? coreLoads = null,
        IReadOnlyList<InterruptDeviceSignal>? devices = null,
        string? wireless = null) =>
        UnifiedDiagnosisGenerator.Generate(
            ports ?? Array.Empty<PortRankResult>(),
            traces ?? Array.Empty<DpcIsrTestResult>(),
            controllers ?? Array.Empty<ControllerSignal>(),
            coreLoads ?? Array.Empty<CoreLoad>(),
            devices ?? Array.Empty<InterruptDeviceSignal>(),
            wireless);

    // --- No data --------------------------------------------------------------------------------

    [Fact]
    public void WithNoDataAtAllItSaysSoAndTellsTheUserWhereToStart()
    {
        var findings = Generate();

        var only = Assert.Single(findings);
        Assert.Equal("No tests run yet", only.Title);
        Assert.Equal(FindingSeverity.Suggestion, only.Severity);
        Assert.Contains("Port test tab", only.RecommendedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoDataItDoesNotInventFindingsFromControllerStateAlone()
    {
        // Controllers exist on every machine. Their mere presence is not a measurement.
        var findings = Generate(controllers: new[] { Controller() });

        Assert.Equal("No tests run yet", findings[0].Title);
    }

    // --- The all-clear case ----------------------------------------------------------------------

    [Fact]
    public void AHealthyMachineGetsAnAllClearThatCitesWhatWasChecked()
    {
        // A bare "nothing found" is indistinguishable from the check having silently skipped the
        // data, so the all-clear has to name the numbers it looked at.
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.32, "1-2") },
            traces: new[] { Trace(0.01) },
            controllers: new[] { Controller() });

        var only = Assert.Single(findings);
        Assert.Equal("Nothing significant found", only.Title);
        Assert.NotEmpty(only.Evidence);
        Assert.Contains(only.Evidence, e => e.StartsWith("Port test:", StringComparison.Ordinal));
        Assert.Contains(only.Evidence, e => e.StartsWith("DPC/ISR:", StringComparison.Ordinal));
        Assert.Contains(only.Evidence, e => e.StartsWith("Affinity:", StringComparison.Ordinal));
    }

    [Fact]
    public void ASinglePortSaysASecondIsNeededRatherThanComparingItToItself()
    {
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1") },
            traces: new[] { Trace(0.01) });

        Assert.Contains(findings[0].Evidence, e => e.Contains("need a second port", StringComparison.Ordinal));
    }

    [Fact]
    public void PortsOfDifferentReportCadenceAreNotComparedAgainstEachOther()
    {
        // A keyboard's jitter sits on a structurally different scale than a mouse's. Treating the two
        // as comparable would report a phantom 10x gap on every machine with both plugged in.
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1", pollingHz: 1000), Port(6.00, "1-2", pollingHz: 25) },
            traces: new[] { Trace(0.01) });

        _output.WriteLine(string.Join("\n", findings.Select(f => f.Title)));
        Assert.Equal("Nothing significant found", findings[0].Title);
    }

    // --- Ranking and capping ---------------------------------------------------------------------

    [Fact]
    public void FindingsAreOrderedBySeverityWithTheMostSevereFirst()
    {
        var findings = Generate(
            ports: new[] { Port(0.20, "1-1"), Port(5.00, "1-2") },
            traces: new[] { Trace(9.0) },
            controllers: new[]
            {
                Controller(policy: InterruptAffinityPolicy.SpecifiedProcessors, pinnedCores: new[] { 0 }, pinnedShare: 70.0),
            },
            coreLoads: new[] { new CoreLoad(0, 70.0), new CoreLoad(1, 5.0) },
            wireless: "2.4 GHz congestion detected on your wireless receiver's channel.");

        var severities = findings.Select(f => f.Severity).ToList();
        Assert.Equal(severities.OrderByDescending(s => s).ToList(), severities);
    }

    [Fact]
    public void NoMoreThanFourFindingsAreEverReturned()
    {
        // The Dashboard shows a plan, not a wall of text.
        var findings = Generate(
            ports: new[] { Port(0.10, "1-1"), Port(9.00, "1-2"), Port(8.00, "1-3"), Port(7.00, "1-4") },
            traces: new[] { Trace(15.0) },
            controllers: new[]
            {
                Controller("A", "PCI\\A\\1", InterruptAffinityPolicy.SpecifiedProcessors, new[] { 0 }, 80.0),
                Controller("B", "PCI\\B\\1", contention: "Shares bandwidth with 3 other devices."),
            },
            coreLoads: new[] { new CoreLoad(0, 80.0), new CoreLoad(1, 2.0) },
            devices: new[] { new InterruptDeviceSignal("GPU", "Display", "nvlddmkm", false, InterruptPriority.Undefined) },
            wireless: "Wireless interference detected.");

        Assert.InRange(findings.Count, 1, 4);
    }

    [Fact]
    public void EveryFindingCarriesATitleExplanationAndAction()
    {
        // The UI binds all three; a null or blank one renders as an empty card.
        var findings = Generate(
            ports: new[] { Port(0.20, "1-1"), Port(5.00, "1-2") },
            traces: new[] { Trace(9.0) },
            controllers: new[] { Controller() });

        Assert.All(findings, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Title));
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(f.RecommendedAction));
            Assert.NotNull(f.Evidence);
        });
    }

    // --- Individual signals ----------------------------------------------------------------------

    [Fact]
    public void AWirelessInterferenceWarningIsSurfaced()
    {
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.31, "1-2") },
            traces: new[] { Trace(0.01) },
            wireless: "Your wireless receiver shares its channel with a busy 2.4 GHz network.");

        Assert.Contains(findings, f =>
            f.Explanation.Contains("wireless", StringComparison.OrdinalIgnoreCase)
            || f.Title.Contains("wireless", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ANullWirelessWarningAddsNothing()
    {
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.31, "1-2") },
            traces: new[] { Trace(0.01) },
            wireless: null);

        Assert.Equal("Nothing significant found", findings[0].Title);
    }

    [Fact]
    public void AClearlyWorsePortIsReported()
    {
        // A 10x gap is far past the 2x bar the port advisor itself uses.
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(3.00, "1-2") },
            traces: new[] { Trace(0.01) });

        Assert.NotEqual("Nothing significant found", findings[0].Title);
    }

    [Fact]
    public void APinnedControllerOnAHotInterruptCoreIsReported()
    {
        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.31, "1-2") },
            traces: new[] { Trace(5.0) },
            controllers: new[]
            {
                Controller(policy: InterruptAffinityPolicy.SpecifiedProcessors, pinnedCores: new[] { 0 }, pinnedShare: 80.0),
            },
            coreLoads: new[] { new CoreLoad(0, 80.0), new CoreLoad(1, 3.0) });

        Assert.NotEqual("Nothing significant found", findings[0].Title);
    }

    // --- Robustness ------------------------------------------------------------------------------

    [Fact]
    public void PortResultsWithNoJitterMeasurementDoNotThrow()
    {
        Exception? failure = Record.Exception(() => Generate(
            ports: new[] { Port(null, "1-1"), Port(null, "1-2") },
            traces: new[] { Trace(0.01) }));

        Assert.Null(failure);
    }

    [Fact]
    public void AMixOfMeasuredAndUnmeasuredPortsDoesNotThrow()
    {
        Exception? failure = Record.Exception(() => Generate(
            ports: new[] { Port(0.30, "1-1"), Port(null, "1-2"), Port(2.00, "1-3") },
            traces: new[] { Trace(1.0) },
            controllers: new[] { Controller() }));

        Assert.Null(failure);
    }

    [Fact]
    public void AZeroJitterBaselineDoesNotProduceInfinityOrNaN()
    {
        // Zero is the divisor in every "how many times worse" comparison.
        var findings = Generate(
            ports: new[] { Port(0.0, "1-1"), Port(5.00, "1-2") },
            traces: new[] { Trace(1.0) });

        Assert.All(findings, f =>
        {
            Assert.DoesNotContain("NaN", f.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("∞", f.Explanation, StringComparison.Ordinal);
            Assert.All(f.Evidence, e =>
            {
                Assert.DoesNotContain("NaN", e, StringComparison.Ordinal);
                Assert.DoesNotContain("∞", e, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public void APinnedCoreAbsentFromTheCoreLoadsDoesNotThrow()
    {
        Exception? failure = Record.Exception(() => Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.31, "1-2") },
            traces: new[] { Trace(1.0) },
            controllers: new[]
            {
                Controller(policy: InterruptAffinityPolicy.SpecifiedProcessors, pinnedCores: new[] { 99 }),
            },
            coreLoads: new[] { new CoreLoad(0, 50.0) }));

        Assert.Null(failure);
    }

    [Fact]
    public void ManyTracesUseTheMostRecentOne()
    {
        var older = Trace(20.0, savedAt: DateTime.Now.AddDays(-2));
        var newer = Trace(0.01);

        var findings = Generate(
            ports: new[] { Port(0.30, "1-1"), Port(0.31, "1-2") },
            traces: new[] { older, newer },
            controllers: new[] { Controller() });

        // The recent quiet trace should win, so the spike-rate evidence cites it rather than the old
        // noisy one.
        Assert.Contains(findings[0].Evidence, e => e.Contains("0.01 spikes/s", StringComparison.Ordinal));
    }

    [Fact]
    public void ALargeHistoryIsProcessedQuickly()
    {
        // This runs on the UI thread every time the Dashboard is shown.
        var random = new Random(11);
        var ports = Enumerable.Range(0, 2_000)
            .Select(i => Port(random.NextDouble() * 3, $"1-{i % 40}"))
            .ToList();
        var traces = Enumerable.Range(0, 500).Select(_ => Trace(random.NextDouble())).ToList();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var findings = Generate(ports: ports, traces: traces, controllers: new[] { Controller() });
        stopwatch.Stop();

        _output.WriteLine($"2000 ports x 500 traces in {stopwatch.ElapsedMilliseconds} ms");
        Assert.NotEmpty(findings);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }
}
