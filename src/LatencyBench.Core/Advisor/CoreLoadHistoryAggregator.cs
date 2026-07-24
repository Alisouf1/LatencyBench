using LatencyBench.Core.DpcIsr;

namespace LatencyBench.Core.Advisor;

/// <param name="Loads">One entry per logical core, always — including cores that serviced no interrupts at all (share 0). Emitting the full set matters: CoreAffinityOptimizer ranks a core it can't find as worst, so an idle core missing from the raw trace data would be treated as the worst choice when it is in fact the best.</param>
/// <param name="TraceCount">How many saved traces carried per-core data and were actually averaged. 0 means the recommendation has no interrupt evidence behind it.</param>
/// <param name="TotalSeconds">Combined traced time behind the average — the real measure of confidence, since ten 2-second traces are weaker evidence than one 60-second one.</param>
public sealed record AggregatedCoreLoads(IReadOnlyList<CoreLoad> Loads, int TraceCount, double TotalSeconds);

/// <summary>
/// Averages the per-core interrupt distribution across every saved DPC/ISR trace, so core assignment
/// rests on accumulated evidence instead of whichever trace ran last.
///
/// A single trace is a short, noisy sample: interrupt distribution shifts with whatever the machine
/// happened to be doing, so consecutive traces disagree, and an optimizer reading only the newest one
/// produces a different answer every time a trace finishes. Averaging fixes that at the source — each
/// additional trace moves the mean less than the one before, so the recommendation converges and
/// stops jumping.
/// </summary>
public static class CoreLoadHistoryAggregator
{
    /// <summary>
    /// Weighted by trace duration, because a 60-second trace is genuinely stronger evidence than a
    /// 5-second one and a plain mean would let the short run count just as much. Traces with no
    /// per-core data (saved before that was recorded) are skipped rather than counted as all-zero,
    /// which would wrongly drag every core's share toward 0.
    /// </summary>
    public static AggregatedCoreLoads Aggregate(IEnumerable<DpcIsrTestResult> traces, int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 0)
        {
            return new AggregatedCoreLoads([], 0, 0);
        }

        var usable = traces.Where(t => t.CoreLoads.Count > 0).ToList();
        if (usable.Count == 0)
        {
            return new AggregatedCoreLoads([], 0, 0);
        }

        var weightedShareByCore = new double[logicalProcessorCount];
        var totalWeight = 0.0;

        foreach (var trace in usable)
        {
            // A zero/absent duration would contribute nothing at all under pure duration weighting and
            // silently drop the trace, so it falls back to counting as one unit.
            var weight = trace.DurationSeconds > 0 ? trace.DurationSeconds : 1.0;
            totalWeight += weight;

            foreach (var load in trace.CoreLoads)
            {
                if (load.CoreIndex >= 0 && load.CoreIndex < logicalProcessorCount)
                {
                    weightedShareByCore[load.CoreIndex] += load.SharePercent * weight;
                }
            }
        }

        // Cores absent from a trace serviced no interrupts in it, so they correctly contributed 0 above
        // and still get an entry here — see the Loads remark on why the full set is emitted.
        var loads = new List<CoreLoad>(logicalProcessorCount);
        for (var core = 0; core < logicalProcessorCount; core++)
        {
            loads.Add(new CoreLoad(core, totalWeight > 0 ? weightedShareByCore[core] / totalWeight : 0));
        }

        return new AggregatedCoreLoads(loads, usable.Count, usable.Sum(t => t.DurationSeconds));
    }
}
