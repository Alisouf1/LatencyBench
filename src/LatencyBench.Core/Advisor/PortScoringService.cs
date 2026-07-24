using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.Core.Advisor;

/// <summary>Turns measured jitter/polling-rate into a PortRank. Pure and deterministic — documented thresholds, not a black box.</summary>
public static class PortScoringService
{
    public static PortRank Score(double jitterMs, double pollingRateHz)
    {
        // These bands assume continuous polling (a mouse in motion). A keyboard's reports are driven
        // by keystrokes, not a fixed poll — its jitter and effective rate measure typing rhythm, a
        // fundamentally different thing, and scoring that against these bands would call any ordinary
        // typing session "Poor". See PortHistoryGrouping.IsHighFrequency for the same distinction.
        if (pollingRateHz < PortHistoryGrouping.HighFrequencyThresholdHz)
        {
            return PortRank.NotApplicable;
        }

        if (jitterMs < 0.15 && pollingRateHz >= 900)
        {
            return PortRank.Excellent;
        }

        if (jitterMs < 0.4 && pollingRateHz >= 400)
        {
            return PortRank.Good;
        }

        if (jitterMs < 1.0 && pollingRateHz >= 150)
        {
            return PortRank.Fair;
        }

        return PortRank.Poor;
    }
}
