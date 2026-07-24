namespace LatencyBench.Core.Advisor;

public readonly record struct DriverStat(string DriverName, double MaxDurationMicroseconds, int SampleCount);

public readonly record struct CoreLoad(int CoreIndex, double SharePercent);

/// <summary>Rule-based, templated diagnosis from a completed DPC/ISR trace — no ML, no network calls, same style as AdvisorMessageGenerator.</summary>
public static class DpcIsrAnalysisGenerator
{
    private const double BorderlineThresholdUs = 100;
    private const double HighThresholdUs = 500;
    private const int MaxDriversListed = 3;

    private const string NoDataMessage =
        "No interrupt activity was captured — start tracing again and let it run for a few seconds under normal use.";

    /// <summary>Past this share on a single core, interrupt servicing is concentrated enough to be worth mentioning alongside the Affinity tab.</summary>
    private const double CoreConcentrationPercent = 60.0;

    public static string Generate(
        IReadOnlyList<DriverStat> driverStats,
        double highestDpcMicroseconds,
        double highestIsrMicroseconds,
        int dpcSampleCount,
        int isrSampleCount,
        IReadOnlyList<CoreLoad>? coreLoads = null)
    {
        if (driverStats.Count == 0)
        {
            return NoDataMessage;
        }

        var overallHighest = Math.Max(highestDpcMicroseconds, highestIsrMicroseconds);
        var verdict = overallHighest switch
        {
            < BorderlineThresholdUs =>
                "Healthy — interrupt latency is well within a safe range for audio/USB real-time work.",
            < HighThresholdUs =>
                "Borderline — some interrupt activity is cutting it close. Watch for audio crackling or USB dropouts.",
            _ =>
                "High latency detected — this is a likely cause of audio crackling, USB dropouts, or input lag.",
        };

        var lines = new List<string>
        {
            verdict,
            string.Empty,
            $"{dpcSampleCount} DPC event(s), highest {highestDpcMicroseconds:0} µs. {isrSampleCount} ISR event(s), highest {highestIsrMicroseconds:0} µs.",
            string.Empty,
            "Top sources:",
        };

        var ranked = driverStats.OrderByDescending(d => d.MaxDurationMicroseconds).Take(MaxDriversListed).ToList();
        foreach (var driver in ranked)
        {
            lines.Add($"  • {driver.DriverName} — {driver.MaxDurationMicroseconds:0} µs max over {driver.SampleCount} event(s) ({SeverityLabel(driver.MaxDurationMicroseconds)})");
        }

        // Concentration on one core isn't automatically a fault — Windows often routes interrupts
        // this way on purpose — so this is stated as a fact plus a conditional action, not a verdict.
        var hottestCore = coreLoads?.OrderByDescending(c => c.SharePercent).FirstOrDefault();
        if (hottestCore is { SharePercent: >= CoreConcentrationPercent } hot)
        {
            lines.Add(string.Empty);
            lines.Add($"CPU cores: core {hot.CoreIndex} handled {hot.SharePercent:0} % of all interrupts. That's normal on its own, but if it's also the core your game or audio app runs on, pinning USB/audio controllers to a quieter core from the Affinity tab may help.");
        }

        var suggestions = ranked
            .Where(d => d.MaxDurationMicroseconds >= BorderlineThresholdUs)
            .Select(d => SuggestFix(d.DriverName))
            .Distinct()
            .ToList();

        if (suggestions.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Suggested fixes:");
            foreach (var suggestion in suggestions)
            {
                lines.Add($"  • {suggestion}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string SeverityLabel(double microseconds) => microseconds switch
    {
        < BorderlineThresholdUs => "healthy",
        < HighThresholdUs => "borderline",
        _ => "high",
    };

    private static string SuggestFix(string driverName)
    {
        var name = driverName.ToLowerInvariant();

        if (name.Contains("unknown"))
        {
            return "One offending driver couldn't be resolved by name — check Device Manager for recently updated drivers, or let the trace run longer to collect more samples.";
        }

        // Shared kernel/framework modules host other vendors' drivers rather than owning the work
        // themselves — they routinely top the list by sheer event count (Wdf01000.sys was 89% of a
        // real 235k-sample trace here). Telling someone to "update Wdf01000.sys" is useless, so say
        // what it actually means instead. Checked first: these are never a vendor's driver.
        if (name.Contains("wdf01000") || name.Contains("ntoskrnl") || name.Contains("ntkrnl") || name.Contains("hal."))
        {
            return "This is a shared Windows kernel framework rather than one vendor's driver — the real cost is in whichever device driver is running on top of it, so check the next-highest source in the list.";
        }

        if (name.Contains("usb") || name.Contains("xhci") || name.Contains("hub"))
        {
            return "Try updating your USB controller/chipset drivers, or disabling USB selective suspend in Power Options.";
        }

        // Match real GPU driver module names, not bare vendor substrings: "amd"/"intel" would also
        // swallow amdppm.sys (CPU power), amdsata.sys (storage), and similar — sending someone to
        // update a graphics driver over a storage stall. Storage/audio checks below would never be
        // reached on an AMD or Intel system otherwise.
        if (name.Contains("nvlddmkm") || name.Contains("nvidia"))
        {
            return "Update your NVIDIA graphics driver — older WDDM drivers are a common source of DPC latency spikes.";
        }

        if (name.Contains("amdkmdag") || name.Contains("amdkmdap") || name.Contains("atikmdag") || name.Contains("atikmpag"))
        {
            return "Update your AMD graphics driver.";
        }

        if (name.Contains("igdkmd") || name.Contains("igdkmdn") || name.Contains("iigd"))
        {
            return "Update your Intel graphics driver.";
        }

        if (name.Contains("rtwlan") || name.Contains("netwtw") || name.Contains("wlan") || name.Contains("wifi") || name.Contains("athw"))
        {
            return "Update your Wi-Fi adapter driver, or try disabling Wi-Fi power-saving mode.";
        }

        // ndis.sys/tcpip.sys are the shared Windows network stack, not a specific vendor's driver —
        // they show up as the offender when the real cost is in the NIC driver underneath them, so
        // point at that rather than at a Windows system file nobody can meaningfully "update".
        if (name.Contains("ndis") || name.Contains("tcpip") || name.Contains("netio"))
        {
            return "This is the Windows network stack — update your network adapter (Ethernet/Wi-Fi) driver, and disable power management on the adapter.";
        }

        // rt6*/rtnic = Realtek NIC, e1d/e2f = Intel NIC. Deliberately not matching bare "realtek":
        // it's ambiguous between the Realtek NIC and Realtek HD Audio (RTKVHD64.sys).
        if (name.Contains("rt6") || name.Contains("rtnic") || name.Contains("e1d") || name.Contains("e2f"))
        {
            return "Update your Ethernet adapter driver, and disable power management on the adapter.";
        }

        if (name.Contains("stor") || name.Contains("nvme") || name.Contains("iastor") || name.Contains("ahci") || name.Contains("sata"))
        {
            return "Update your storage controller (AHCI/NVMe/SATA) driver.";
        }

        if (name.Contains("hdaudio") || name.Contains("rtkvhd") || name.Contains("audio"))
        {
            return "Update your audio driver.";
        }

        return $"Check Device Manager for driver updates related to {driverName}.";
    }
}
