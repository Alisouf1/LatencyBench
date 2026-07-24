namespace LatencyBench.Core.Advisor;

/// <summary>
/// Recommends which CPU core a USB controller's interrupts should be pinned to.
///
/// The important subtlety: CPU usage is NOT interrupt load. Interrupts are so short they barely
/// register as CPU time, so the core servicing most of the system's interrupts can still read 0%
/// busy — and recommending it is exactly backwards. On this developer's own machine core 0 showed
/// 0% CPU while handling 67% of all interrupts. So interrupt distribution (from a DPC/ISR trace) is
/// used when it's available, and when it isn't we say so rather than guess from CPU usage alone.
/// </summary>
public static class CoreAffinityAdvisor
{
    /// <summary>Within this many percentage points of the least-busy core, a pinned core counts as "good enough" rather than prompting a switch.</summary>
    private const double GoodEnoughToleranceUsagePercent = 5.0;

    /// <summary>Cores whose CPU usage is within this much of the minimum are indistinguishable — picking the numerically-first one would be false precision.</summary>
    private const double UsageTieTolerancePercent = 1.0;

    /// <summary>Past this share of the system's interrupts, a core is a poor place to put more of them.</summary>
    private const double InterruptHotSharePercent = 25.0;

    public static string Generate(
        IReadOnlyList<double> coreUsagePercent,
        IReadOnlyList<int> pinnedCoreIndices,
        IReadOnlyList<CoreLoad>? interruptLoads = null)
    {
        // Interrupt distribution is checked first and on its own: it's the signal that actually
        // matters here, and it stays valid even when every core reads 0% CPU — which is the normal
        // case, since interrupts are far too short to show up as CPU time.
        if (interruptLoads is { Count: > 0 })
        {
            return GenerateFromInterruptLoad(pinnedCoreIndices, interruptLoads);
        }

        if (coreUsagePercent.Count == 0 || coreUsagePercent.All(u => u <= 0.01))
        {
            return "Collecting core usage data…";
        }

        return GenerateFromCpuUsage(coreUsagePercent, pinnedCoreIndices);
    }

    /// <summary>The good case: a DPC/ISR trace has told us where interrupts actually land.</summary>
    private static string GenerateFromInterruptLoad(IReadOnlyList<int> pinnedCoreIndices, IReadOnlyList<CoreLoad> interruptLoads)
    {
        var quietest = interruptLoads.OrderBy(c => c.SharePercent).First();

        if (pinnedCoreIndices.Count == 0)
        {
            return $"Using Windows' default core assignment. From the last DPC/ISR trace, core {quietest.CoreIndex} services the fewest interrupts ({quietest.SharePercent:0}% of them) — pinning this controller there may give steadier timing.";
        }

        var pinnedLabel = pinnedCoreIndices.Count == 1 ? $"Core {pinnedCoreIndices[0]}" : $"Cores {string.Join(", ", pinnedCoreIndices)}";
        var hottestPinned = interruptLoads
            .Where(c => pinnedCoreIndices.Contains(c.CoreIndex))
            .OrderByDescending(c => c.SharePercent)
            .FirstOrDefault();

        if (hottestPinned.SharePercent >= InterruptHotSharePercent)
        {
            return $"{pinnedLabel} pinned — but the last DPC/ISR trace shows core {hottestPinned.CoreIndex} already services {hottestPinned.SharePercent:0}% of the system's interrupts. Core {quietest.CoreIndex} is far quieter ({quietest.SharePercent:0}%).";
        }

        return $"{pinnedLabel} pinned, servicing {hottestPinned.SharePercent:0}% of the system's interrupts — a quiet choice.";
    }

    /// <summary>
    /// The fallback: no trace has been run, so only CPU usage is known. That's a weak signal for this
    /// decision, so it never names a core unless one is genuinely, measurably idler than the rest.
    /// </summary>
    private static string GenerateFromCpuUsage(IReadOnlyList<double> coreUsagePercent, IReadOnlyList<int> pinnedCoreIndices)
    {
        var best = coreUsagePercent
            .Select((usage, index) => (Index: index, Usage: usage))
            .OrderBy(c => c.Usage)
            .First();

        var tiedCount = coreUsagePercent.Count(u => u <= best.Usage + UsageTieTolerancePercent);
        var ambiguous = tiedCount > 1;

        if (pinnedCoreIndices.Count == 0)
        {
            return ambiguous
                ? $"Using Windows' default core assignment. {tiedCount} cores are equally idle on CPU usage, and that can't tell them apart — interrupts are too short to show up as CPU time. Run a trace on the DPC/ISR tab and this will recommend a core based on where interrupts actually land."
                : $"Using Windows' default core assignment. Core {best.Index} is currently the least busy ({best.Usage:0}%) — pinning this controller to it may give steadier interrupt timing.";
        }

        var pinnedUsage = pinnedCoreIndices
            .Select(i => i >= 0 && i < coreUsagePercent.Count ? coreUsagePercent[i] : 0)
            .Average();

        var pinnedLabel = pinnedCoreIndices.Count == 1 ? $"Core {pinnedCoreIndices[0]}" : $"Cores {string.Join(", ", pinnedCoreIndices)}";

        if (ambiguous || pinnedCoreIndices.Contains(best.Index) || pinnedUsage <= best.Usage + GoodEnoughToleranceUsagePercent)
        {
            return $"{pinnedLabel} pinned, averaging {pinnedUsage:0}% CPU usage. Run a trace on the DPC/ISR tab to check it isn't a core that's already busy servicing interrupts — CPU usage alone won't show that.";
        }

        return $"{pinnedLabel} pinned, averaging {pinnedUsage:0}% usage. Core {best.Index} is currently less busy ({best.Usage:0}%) — consider switching to it for steadier timing.";
    }
}
