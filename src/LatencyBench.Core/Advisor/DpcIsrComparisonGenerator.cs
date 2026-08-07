using LatencyBench.Core.DpcIsr;

namespace LatencyBench.Core.Advisor;

/// <summary>
/// Compares a finished DPC/ISR trace against previously saved ones, using the duration-independent
/// spike rate. Deliberately conservative: interrupt load depends heavily on what the machine was
/// actually doing, so only a large, repeated difference is called real.
/// </summary>
public static class DpcIsrComparisonGenerator
{
    /// <summary>
    /// Spike rates swing a lot between runs simply because the workload differs — a busy firefight
    /// is not a quiet corridor. The bar for calling a change real is therefore much higher than for
    /// port jitter, and even then it's framed as "suggests", not "proves".
    /// </summary>
    private const double NoiseTolerancePercent = 25.0;

    /// <summary>Below this many spikes per second, the difference between two runs is not worth interpreting.</summary>
    private const double NegligibleRate = 0.05;

    public static string Generate(DpcIsrTestResult current, IReadOnlyList<DpcIsrTestResult> history)
    {
        var sameConfig = history
            .Where(r => r.ConfigLabel == current.ConfigLabel)
            .ToList();
        var otherConfigs = history
            .Where(r => r.ConfigLabel != current.ConfigLabel)
            .ToList();

        if (otherConfigs.Count == 0)
        {
            return sameConfig.Count == 0
                ? $"First saved trace ({current.ConfigLabel}). Change a setting, trace again for the same length, and the difference will be measured here."
                : $"{sameConfig.Count + 1} traces saved, all with the same config ({current.ConfigLabel}). " +
                  $"Your spread on this config so far: {Spread(sameConfig.Append(current).ToList())}. " +
                  "Change a setting and trace again to compare.";
        }

        // Chosen by the config's AVERAGE, not by whichever single saved run happened to be lowest.
        // Selecting on one run then reporting that config's average is self-contradictory, and it
        // misleads in the worst direction: a config with one lucky run and one terrible one would be
        // picked over a consistently better one, and the user then compared against the lucky
        // config's much worse average. Measured before this changed - current 5.00 against configs
        // averaging 10.05 ("Lucky", runs 0.10 and 20.00) and 1.10 ("Steady", runs 1.00 and 1.20) -
        // it selected Lucky and reported "50% fewer spikes, a real improvement", when the current
        // config was in fact four and a half times worse than the best config on record.
        //
        // Averaging is this class's whole defence against run-to-run noise; selecting on a single run
        // discarded it at the first step.
        var bestOtherGroup = otherConfigs
            .GroupBy(r => r.ConfigLabel, StringComparer.Ordinal)
            .OrderBy(g => g.Average(r => r.HighSpikesPerSecond))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();

        var currentGroup = sameConfig.Append(current).ToList();
        var currentAvg = currentGroup.Average(r => r.HighSpikesPerSecond);
        var otherGroup = bestOtherGroup.ToList();
        var otherAvg = otherGroup.Average(r => r.HighSpikesPerSecond);

        var lines = new List<string>
        {
            $"This config ({current.ConfigLabel}): {currentAvg:0.00} spikes/s over 500 µs, from {currentGroup.Count} trace(s). {Spread(currentGroup)}",
            $"Compared to ({bestOtherGroup.Key}): {otherAvg:0.00} spikes/s, from {otherGroup.Count} trace(s). {Spread(otherGroup)}",
            string.Empty,
        };

        if (currentAvg <= NegligibleRate && otherAvg <= NegligibleRate)
        {
            lines.Add("Both configs are essentially spike-free — there's nothing to choose between them.");
            return string.Join("\n", lines);
        }

        var baseline = Math.Max(otherAvg, NegligibleRate);
        var change = (currentAvg - otherAvg) / baseline * 100.0;

        if (Math.Abs(change) < NoiseTolerancePercent)
        {
            lines.Add($"Difference is {Math.Abs(change):0}% — within the run-to-run swing you'd expect from different gameplay alone. No measurable effect.");
        }
        else if (change < 0)
        {
            lines.Add($"This config shows {Math.Abs(change):0}% fewer spikes. That suggests a real improvement.");
        }
        else
        {
            lines.Add($"This config shows {change:0}% more spikes. That suggests it's worse.");
        }

        if (currentGroup.Count < 2 || otherGroup.Count < 2)
        {
            lines.Add("Careful: at least one config only has a single trace. Interrupt load follows whatever the machine was doing, so run 2-3 traces per config, of the same length, doing the same thing.");
        }

        return string.Join("\n", lines);
    }

    private static string Spread(IReadOnlyList<DpcIsrTestResult> group)
    {
        if (group.Count < 2)
        {
            return string.Empty;
        }

        var rates = group.Select(r => r.HighSpikesPerSecond).ToList();
        return $"(range {rates.Min():0.00}-{rates.Max():0.00})";
    }
}
