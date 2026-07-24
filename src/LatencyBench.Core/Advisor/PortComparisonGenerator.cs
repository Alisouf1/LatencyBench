using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.Core.Advisor;

public enum ComparisonVerdict
{
    NoBaseline,
    Improved,
    Regressed,
    Unchanged,
}

public readonly record struct PortComparison(
    ComparisonVerdict Verdict,
    double? PreviousJitterMs,
    double CurrentJitterMs,
    double PercentChange,
    string Message);

/// <summary>
/// Compares a just-finished test against the best previously saved run for the same physical port,
/// so a change (MSI mode, IRQ priority, core affinity, a tweak) can be shown to have actually helped
/// rather than assumed. Rule-based and pure, like the other advisors.
/// </summary>
public static class PortComparisonGenerator
{
    /// <summary>
    /// Run-to-run noise on the same port is easily a few percent, so only a change past this is
    /// called a real improvement/regression. Claiming a 3% shift is a win would be overclaiming.
    /// </summary>
    private const double NoiseTolerancePercent = 10.0;

    public static PortComparison Compare(double currentJitterMs, string portLocation, IReadOnlyList<PortRankResult> history, bool underLoad = false, bool isHighFrequency = true)
    {
        // Like-for-like only: a run under CPU load is expected to be worse than an idle one, so
        // comparing across conditions would report a regression that isn't real. Same reasoning for
        // report cadence — a keyboard's baseline jitter is structurally larger than a mouse's, so a
        // keyboard result must never be measured against (or overwrite) a mouse's baseline here.
        var previousBest = history
            .Where(r => string.Equals(r.PortLocation, portLocation, StringComparison.OrdinalIgnoreCase)
                && r.AverageJitterMs is not null
                && r.UnderLoad == underLoad
                && PortHistoryGrouping.IsHighFrequency(r) == isHighFrequency)
            .OrderBy(r => r.AverageJitterMs)
            .FirstOrDefault();

        if (previousBest?.AverageJitterMs is not double previous || previous <= 0)
        {
            var condition = underLoad ? " under CPU load" : "";
            return new PortComparison(
                ComparisonVerdict.NoBaseline,
                null,
                currentJitterMs,
                0,
                $"First saved result for this port{condition}. Change a setting (MSI mode, IRQ priority, core affinity), test again, and the improvement will be measured here.");
        }

        var percentChange = (currentJitterMs - previous) / previous * 100.0;

        if (percentChange <= -NoiseTolerancePercent)
        {
            return new PortComparison(
                ComparisonVerdict.Improved,
                previous,
                currentJitterMs,
                percentChange,
                $"Better — jitter dropped from {previous:0.000} ms to {currentJitterMs:0.000} ms ({Math.Abs(percentChange):0} % lower) than your best on this port. Whatever you changed helped.");
        }

        if (percentChange >= NoiseTolerancePercent)
        {
            return new PortComparison(
                ComparisonVerdict.Regressed,
                previous,
                currentJitterMs,
                percentChange,
                $"Worse — jitter rose from {previous:0.000} ms to {currentJitterMs:0.000} ms ({percentChange:0} % higher) than your best on this port. Consider undoing the last change.");
        }

        return new PortComparison(
            ComparisonVerdict.Unchanged,
            previous,
            currentJitterMs,
            percentChange,
            $"About the same as your best on this port ({currentJitterMs:0.000} ms vs {previous:0.000} ms) — that gap is within normal run-to-run variation, so the change made no measurable difference.");
    }
}
