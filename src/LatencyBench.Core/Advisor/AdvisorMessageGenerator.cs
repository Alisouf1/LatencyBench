using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.Core.Advisor;

/// <summary>Rule-based, templated recommendation text from real test results — no ML, no network calls.</summary>
public static class AdvisorMessageGenerator
{
    private const string NoResultsMessage =
        "The advisor will rank your ports and recommend the best one here once you've run a port test.";

    public static string Generate(IReadOnlyList<PortRankResult> results)
    {
        // Restricted to continuously-polling devices (mice, effectively) — a keyboard's jitter is
        // measured from sporadic keystroke timing, structurally larger than a mouse's, and comparing
        // the two as "which port is best" would flag a perfectly ordinary keyboard as the worst port
        // every single time. See PortHistoryGrouping.IsHighFrequency.
        var tested = results.Where(r => r.Rank != PortRank.NotTested && r.AverageJitterMs is not null && PortHistoryGrouping.IsHighFrequency(r)).ToList();
        if (tested.Count == 0)
        {
            return NoResultsMessage;
        }

        var best = tested.OrderBy(r => r.AverageJitterMs).First();
        if (tested.Count == 1)
        {
            return $"{best.PortLabel} — {FormatJitter(best)}{FormatPolling(best)}. That's the only port tested so far.";
        }

        var worst = tested.OrderByDescending(r => r.AverageJitterMs).First();
        if (ReferenceEquals(best, worst))
        {
            return $"{best.PortLabel} — {FormatJitter(best)}{FormatPolling(best)}.";
        }

        var worstIsNotablyWorse = worst.AverageJitterMs >= (best.AverageJitterMs ?? 0) * 2;
        if (worstIsNotablyWorse)
        {
            return $"Use {best.PortLabel} — {FormatJitter(best)}{FormatPolling(best)}. " +
                   $"{worst.PortLabel} is comparatively poor ({FormatJitter(worst)}) — consider moving latency-sensitive devices off it.";
        }

        return $"Use {best.PortLabel} — {FormatJitter(best)}{FormatPolling(best)}.";
    }

    private static string FormatJitter(PortRankResult result) => $"lowest jitter at {result.AverageJitterMs:0.00} ms";

    private static string FormatPolling(PortRankResult result)
        => result.PollingRateHz is int hz ? $" and a {hz} Hz poll" : string.Empty;
}
