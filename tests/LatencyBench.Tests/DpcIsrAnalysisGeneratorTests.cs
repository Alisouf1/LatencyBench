using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the DPC/ISR diagnosis text, which had no tests despite being what the user actually reads
/// and acts on after a trace. A wrong classification here does not crash anything - it sends someone
/// to update the wrong driver, which is worse than saying nothing.
///
/// Driver names are the real module names Windows reports, not invented ones, because the
/// classification is substring matching over exactly those strings.
/// </summary>
public sealed class DpcIsrAnalysisGeneratorTests
{
    private static string Generate(
        IEnumerable<DriverStat> drivers,
        double highestDpc = 0,
        double highestIsr = 0,
        int dpcCount = 0,
        int isrCount = 0,
        IReadOnlyList<CoreLoad>? coreLoads = null) =>
        DpcIsrAnalysisGenerator.Generate(
            drivers.ToList(), highestDpc, highestIsr, dpcCount, isrCount, coreLoads);

    private static string ForDriver(string name, double maxUs = 900) =>
        Generate(new[] { new DriverStat(name, maxUs, 10) }, highestDpc: maxUs, dpcCount: 10);

    /// <summary>
    /// Just the "Suggested fixes" block. The "Top sources" block echoes the driver name verbatim, so
    /// comparing whole outputs conflates the advice with the spelling of the module that triggered it.
    /// </summary>
    private static string SuggestionsOnly(string text)
    {
        int start = text.IndexOf("Suggested fixes:", StringComparison.Ordinal);
        return start < 0 ? string.Empty : text[start..];
    }

    /// <summary>Bullets in the "Top sources" block only - the suggestion block uses bullets too.</summary>
    private static int CountSourceBullets(string text)
    {
        int start = text.IndexOf("Top sources:", StringComparison.Ordinal);
        if (start < 0)
        {
            return 0;
        }

        int end = text.IndexOf("Suggested fixes:", start, StringComparison.Ordinal);
        string block = end < 0 ? text[start..] : text[start..end];

        return block.Split('\n').Count(line => line.TrimStart().StartsWith("•", StringComparison.Ordinal));
    }

    // --- Empty input -----------------------------------------------------------------------------

    [Fact]
    public void NoDriverStatsProducesTheNoDataMessageRatherThanAnEmptyVerdict()
    {
        string text = Generate(Array.Empty<DriverStat>());

        Assert.Contains("No interrupt activity was captured", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Top sources", text, StringComparison.Ordinal);
    }

    // --- Verdict thresholds ----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "Healthy")]
    [InlineData(99.9, "Healthy")]
    [InlineData(100, "Borderline")]     // boundary: >= 100 is no longer healthy
    [InlineData(499.9, "Borderline")]
    [InlineData(500, "High latency")]   // boundary: >= 500 is high
    [InlineData(5000, "High latency")]
    public void TheVerdictFollowsTheDocumentedThresholds(double highestUs, string expected)
    {
        string text = Generate(
            new[] { new DriverStat("something.sys", highestUs, 1) },
            highestDpc: highestUs,
            dpcCount: 1);

        Assert.StartsWith(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictUsesWhicheverOfDpcOrIsrIsWorse()
    {
        // A low DPC peak must not mask a high ISR peak.
        string text = Generate(
            new[] { new DriverStat("x.sys", 900, 1) },
            highestDpc: 10,
            highestIsr: 900,
            dpcCount: 1,
            isrCount: 1);

        Assert.StartsWith("High latency", text, StringComparison.Ordinal);
    }

    // --- Ranking ---------------------------------------------------------------------------------

    [Fact]
    public void SourcesAreRankedByWorstPeakNotBySampleCount()
    {
        // The whole point is finding the driver that stalls longest, not the chattiest one.
        string text = Generate(new[]
        {
            new DriverStat("chatty.sys", 50, 100_000),
            new DriverStat("theculprit.sys", 1200, 3),
        }, highestDpc: 1200, dpcCount: 100_003);

        int culprit = text.IndexOf("theculprit.sys", StringComparison.Ordinal);
        int chatty = text.IndexOf("chatty.sys", StringComparison.Ordinal);

        Assert.True(culprit >= 0 && chatty >= 0, "both drivers should be listed");
        Assert.True(culprit < chatty, "the worst peak must be listed first");
    }

    [Fact]
    public void AtMostThreeSourcesAreListed()
    {
        var drivers = Enumerable.Range(1, 10)
            .Select(i => new DriverStat($"driver{i}.sys", i * 100, 5))
            .ToList();

        string text = Generate(drivers, highestDpc: 1000, dpcCount: 50);

        Assert.Equal(3, CountSourceBullets(text));
        Assert.Contains("driver10.sys", text, StringComparison.Ordinal);  // worst is kept
        Assert.DoesNotContain("driver1.sys —", text, StringComparison.Ordinal); // weakest dropped
    }

    // --- Shared kernel modules must not be blamed ------------------------------------------------

    [Theory]
    [InlineData("Wdf01000.sys")]
    [InlineData("ntoskrnl.exe")]
    [InlineData("ntkrnlmp.exe")]
    [InlineData("hal.dll")]
    public void SharedKernelModulesAreExplainedRatherThanBlamed(string driverName)
    {
        // Telling someone to "update Wdf01000.sys" is useless - it hosts other vendors' drivers.
        string text = ForDriver(driverName);

        Assert.Contains("shared Windows kernel framework", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Update your", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ndis.sys")]
    [InlineData("tcpip.sys")]
    [InlineData("netio.sys")]
    public void TheSharedNetworkStackPointsAtTheAdapterDriver(string driverName)
    {
        string text = ForDriver(driverName);

        Assert.Contains("Windows network stack", text, StringComparison.Ordinal);
        Assert.Contains("network adapter", text, StringComparison.Ordinal);
    }

    // --- Device families -------------------------------------------------------------------------

    [Theory]
    [InlineData("USBXHCI.SYS")]
    [InlineData("usbhub3.sys")]
    [InlineData("iusb3xhc.sys")]
    public void UsbControllersGetTheUsbSuggestion(string driverName)
    {
        Assert.Contains("USB controller", ForDriver(driverName), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nvlddmkm.sys", "NVIDIA")]
    [InlineData("amdkmdag.sys", "AMD")]
    [InlineData("atikmdag.sys", "AMD")]
    [InlineData("igdkmd64.sys", "Intel")]
    public void GraphicsDriversAreAttributedToTheRightVendor(string driverName, string vendor)
    {
        string text = ForDriver(driverName);

        Assert.Contains(vendor, text, StringComparison.Ordinal);
        Assert.Contains("graphics driver", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason vendor matching uses real module names rather than bare vendor substrings. Matching
    /// "amd" would swallow amdppm.sys (CPU power management) and amdsata.sys (storage), sending
    /// someone to update a graphics driver over a storage stall - and on an AMD or Intel system the
    /// storage and audio checks below would never be reached at all.
    /// </summary>
    [Theory]
    [InlineData("amdppm.sys")]
    [InlineData("intelppm.sys")]
    public void CpuPowerDriversAreNotMistakenForGraphicsDrivers(string driverName)
    {
        string text = ForDriver(driverName);

        Assert.DoesNotContain("graphics driver", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("amdsata.sys")]
    [InlineData("stornvme.sys")]
    [InlineData("iaStorAC.sys")]
    [InlineData("storahci.sys")]
    public void StorageDriversGetTheStorageSuggestion(string driverName)
    {
        string text = ForDriver(driverName);

        Assert.Contains("storage controller", text, StringComparison.Ordinal);
        Assert.DoesNotContain("graphics driver", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Netwtw10.sys")]
    [InlineData("rtwlane.sys")]
    [InlineData("athwnx.sys")]
    public void WirelessAdaptersGetTheWifiSuggestion(string driverName)
    {
        Assert.Contains("Wi-Fi", ForDriver(driverName), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rt640x64.sys")]
    [InlineData("e1d68x64.sys")]
    public void EthernetAdaptersGetTheEthernetSuggestion(string driverName)
    {
        string text = ForDriver(driverName);

        Assert.Contains("Ethernet adapter", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RTKVHD64.sys")]
    [InlineData("hdaudio.sys")]
    public void AudioDriversGetTheAudioSuggestion(string driverName)
    {
        Assert.Contains("audio driver", ForDriver(driverName), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedDriverSaysSoRatherThanNamingAFile()
    {
        string text = ForDriver("unknown");

        Assert.Contains("couldn't be resolved by name", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedDriverFallsBackToNamingItExplicitly()
    {
        string text = ForDriver("SomeVendorWidget.sys");

        Assert.Contains("SomeVendorWidget.sys", text, StringComparison.Ordinal);
        Assert.Contains("Device Manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Windows reports module names in inconsistent casing. Only the advice is compared: the
        // "Top sources" block echoes the name as given, so the full texts legitimately differ.
        Assert.Equal(
            SuggestionsOnly(ForDriver("NVLDDMKM.SYS")),
            SuggestionsOnly(ForDriver("nvlddmkm.sys")));
        Assert.Contains("NVIDIA", SuggestionsOnly(ForDriver("NVLDDMKM.SYS")), StringComparison.Ordinal);
    }

    // --- Suggestions are gated on severity -------------------------------------------------------

    [Fact]
    public void HealthyDriversProduceNoSuggestions()
    {
        // Nothing is wrong, so proposing driver updates would be noise.
        string text = Generate(
            new[] { new DriverStat("nvlddmkm.sys", 40, 100) },
            highestDpc: 40,
            dpcCount: 100);

        Assert.DoesNotContain("Suggested fixes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyDriversAtOrAboveTheBorderlineThresholdAreSuggestedFor()
    {
        string text = Generate(new[]
        {
            new DriverStat("nvlddmkm.sys", 800, 10),   // high - should be suggested for
            new DriverStat("RTKVHD64.sys", 20, 500),   // healthy - should not
        }, highestDpc: 800, dpcCount: 510);

        Assert.Contains("NVIDIA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("audio driver", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalSuggestionsAreNotRepeated()
    {
        // Two USB modules must not produce the same paragraph twice.
        string text = Generate(new[]
        {
            new DriverStat("usbxhci.sys", 900, 10),
            new DriverStat("usbhub3.sys", 800, 10),
        }, highestDpc: 900, dpcCount: 20);

        int occurrences = text.Split("USB controller").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // --- Core concentration ----------------------------------------------------------------------

    [Fact]
    public void AHotCoreIsMentionedWithoutBeingCalledAFault()
    {
        string text = Generate(
            new[] { new DriverStat("x.sys", 900, 10) },
            highestDpc: 900,
            dpcCount: 10,
            coreLoads: new[] { new CoreLoad(3, 82.0), new CoreLoad(0, 18.0) });

        Assert.Contains("core 3", text, StringComparison.Ordinal);
        Assert.Contains("82 %", text, StringComparison.Ordinal);
        Assert.Contains("normal on its own", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EvenlySpreadCoresAreNotMentioned()
    {
        string text = Generate(
            new[] { new DriverStat("x.sys", 900, 10) },
            highestDpc: 900,
            dpcCount: 10,
            coreLoads: new[] { new CoreLoad(0, 30.0), new CoreLoad(1, 35.0), new CoreLoad(2, 35.0) });

        Assert.DoesNotContain("CPU cores:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConcentrationThresholdIsInclusive()
    {
        string text = Generate(
            new[] { new DriverStat("x.sys", 900, 10) },
            highestDpc: 900,
            dpcCount: 10,
            coreLoads: new[] { new CoreLoad(1, 60.0) });

        Assert.Contains("core 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCoreLoadsAreHandled()
    {
        Exception? failure = Record.Exception(() => Generate(
            new[] { new DriverStat("x.sys", 900, 10) },
            highestDpc: 900,
            dpcCount: 10,
            coreLoads: null));

        Assert.Null(failure);
    }

    [Fact]
    public void AnEmptyCoreLoadListIsHandled()
    {
        // FirstOrDefault over an empty list yields default(CoreLoad) - share 0 - which must not be
        // reported as "core 0 handled 0% of interrupts".
        string text = Generate(
            new[] { new DriverStat("x.sys", 900, 10) },
            highestDpc: 900,
            dpcCount: 10,
            coreLoads: Array.Empty<CoreLoad>());

        Assert.DoesNotContain("CPU cores:", text, StringComparison.Ordinal);
    }

    // --- Counts ----------------------------------------------------------------------------------

    [Fact]
    public void EventCountsAndPeaksAreReportedForBothKinds()
    {
        string text = Generate(
            new[] { new DriverStat("x.sys", 640, 10) },
            highestDpc: 640.4,
            highestIsr: 120.6,
            dpcCount: 1234,
            isrCount: 567);

        Assert.Contains("1234 DPC event(s)", text, StringComparison.Ordinal);
        Assert.Contains("640 µs", text, StringComparison.Ordinal);
        Assert.Contains("567 ISR event(s)", text, StringComparison.Ordinal);
        Assert.Contains("121 µs", text, StringComparison.Ordinal);
    }
}
