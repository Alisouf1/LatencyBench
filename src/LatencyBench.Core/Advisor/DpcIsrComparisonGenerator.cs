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

        var bestOther = otherConfigs.OrderBy(r => r.HighSpikesPerSecond).First();
        var currentGroup = sameConfig.Append(current).ToList();
        var currentAvg = currentGroup.Average(r => r.HighSpikesPerSecond);
        var otherGroup = otherConfigs.Where(r => r.ConfigLabel == bestOther.ConfigLabel).ToList();
        var otherAvg = otherGroup.Average(r => r.HighSpikesPerSecond);

        var lines = new List<string>
        {
            $"This config ({current.ConfigLabel}): {currentAvg:0.00} spikes/s over 500 µs, from {currentGroup.Count} trace(s). {Spread(currentGroup)}",
            $"Compared to ({bestOther.ConfigLabel}): {otherAvg:0.00} spikes/s, from {otherGroup.Count} trace(s). {Spread(otherGroup)}",
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
