namespace LatencyBench.Core.Advisor;

/// <summary>
/// Picks one concrete core index for "Optimize all" to pin a device to, and — crucially —
/// deterministically: the same inputs always produce the same assignment, so clicking Optimize twice
/// in a row doesn't reshuffle everything. It tracks which cores this pass already handed to an earlier
/// device, so five devices don't all get piled onto whichever one core looked quietest in isolation,
/// which would just recreate the hot-core problem one core over.
/// </summary>
public static class CoreAffinityOptimizer
{
    /// <summary>Core 0 fields the clock interrupt and a disproportionate share of default-routed system work — avoided unless every other core is already spoken for.</summary>
    public const int AvoidCoreIndex = 0;

    public static int? RecommendCore(
        int logicalProcessorCount,
        IReadOnlyList<CoreLoad>? interruptLoads,
        ISet<int> alreadyClaimedThisPass)
    {
        if (logicalProcessorCount <= 0)
        {
            return null;
        }

        var allCores = Enumerable.Range(0, logicalProcessorCount).ToList();
        var ranked = Rank(allCores, interruptLoads);
        return PickBest(ranked, alreadyClaimedThisPass);
    }

    /// <summary>
    /// With a DPC/ISR trace loaded, cores are ranked by how few interrupts they service — the signal
    /// that actually matters (see CoreAffinityAdvisor: interrupts are too short to register as CPU
    /// usage), and stable between clicks because a saved trace doesn't change on its own. Ties break by
    /// core index so the order is fully determined. With no trace, cores are left in natural index
    /// order (0,1,2,…) and PickBest spreads devices across them round-robin.
    ///
    /// Deliberately NOT based on live CPU usage: an instantaneous CPU sample reads differently every
    /// second, so ranking by it made every click of Optimize produce a different assignment — the exact
    /// confusing, untrustworthy behavior this rewrite removes.
    /// </summary>
    private static List<int> Rank(List<int> allCores, IReadOnlyList<CoreLoad>? interruptLoads)
    {
        if (interruptLoads is { Count: > 0 })
        {
            var shareByCore = interruptLoads.ToDictionary(l => l.CoreIndex, l => l.SharePercent);
            return allCores
                .OrderBy(i => shareByCore.TryGetValue(i, out var share) ? share : double.MaxValue)
                .ThenBy(i => i)
                .ToList();
        }

        // Already 0,1,2,… — a deterministic basis for the round-robin spread in PickBest.
        return allCores;
    }

    /// <summary>Walk the ranked list, skipping cores this pass already handed to an earlier device and — while any alternative remains — core 0. Falls back to reuse rather than fail once every core is claimed (few-core machines included).</summary>
    private static int PickBest(List<int> rankedCores, ISet<int> alreadyClaimed)
    {
        var free = rankedCores.Where(c => !alreadyClaimed.Contains(c)).ToList();

        var choice = free.FirstOrDefault(c => c != AvoidCoreIndex, free.Count > 0 ? free[0] : rankedCores[0]);

        alreadyClaimed.Add(choice);
        return choice;
    }
}
