using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Recommendations.Rules;

namespace LatencyBench.Core.Recommendations;

/// <summary>The full result of one analysis pass.</summary>
public sealed class RecommendationReport
{
    /// <summary>Recommendations that fired, ranked most worth doing first.</summary>
    public required IReadOnlyList<Recommendation> Recommendations { get; init; }

    /// <summary>
    /// Rules that examined this machine and decided nothing was needed, with their reasons. Surfaced
    /// rather than discarded so the user can see the whole analysis rather than only its output.
    /// </summary>
    public required IReadOnlyList<RuleOutcome.NotApplicable> NotApplicable { get; init; }

    /// <summary>Rules that could not decide, and what they were missing. These are the prompts that
    /// tell a user which measurement to take next.</summary>
    public required IReadOnlyList<RuleOutcome.Undetermined> Undetermined { get; init; }

    /// <summary>Rules that threw. A broken rule must not take down the whole analysis.</summary>
    public required IReadOnlyList<string> Failures { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Recommendations safe enough for a profile to apply without the user picking each one.</summary>
    public IReadOnlyList<Recommendation> AutoApplicable => Recommendations
        .Where(recommendation =>
            recommendation.Safety.IsAutoApplicable
            && !recommendation.HasBlockingConflict
            && recommendation.Action is RecommendedAction.ApplyTweak)
        .ToList();
}

/// <summary>
/// Runs every rule against a snapshot of the machine and ranks what comes back.
/// </summary>
public sealed class RecommendationEngine
{
    private readonly IReadOnlyList<IRecommendationRule> _rules;
    private readonly ConflictDetector _conflictDetector;

    public RecommendationEngine(IEnumerable<IRecommendationRule>? rules = null, ConflictDetector? conflictDetector = null)
    {
        _rules = rules?.ToList() ?? DefaultRules();
        _conflictDetector = conflictDetector ?? new ConflictDetector();
    }

    public static IReadOnlyList<IRecommendationRule> DefaultRules() => new IRecommendationRule[]
    {
		// Ordered roughly by how fundamental the finding is. Ranking happens afterwards, so this
		// order only decides tie-breaks between equal scores.
		new VirtualMachineRule(),
        new PlatformClockRule(),
        new MemoryIntegrityRule(),
        new DriverLatencyRule(),
        new UsbControllerAffinityRule(),
        new MsiModeRule(),
        new HighPerformancePlanRule(),
        new CoreParkingRule(),
        new ProcessorMinimumStateRule(),
        new UsbSelectiveSuspendRule(),
        new UsbHubPowerManagementRule(),
        new HardwareGpuSchedulingRule(),
        new MmcssGamesProfileRule(),
        new TimerResolutionRule(),
        new NetworkPowerManagementRule(),
        new NagleAlgorithmRule(),
        new NetworkThrottlingRule(),
        new PointerPrecisionRule(),
        new MemorySpeedRule(),
        new SchedulerPrioritySeparationRule(),
        new LastAccessTimestampRule()
    };

    public RecommendationReport Analyze(RecommendationContext context)
    {
        var applicable = new List<Recommendation>();
        var notApplicable = new List<RuleOutcome.NotApplicable>();
        var undetermined = new List<RuleOutcome.Undetermined>();
        var failures = new List<string>();

        foreach (IRecommendationRule rule in _rules)
        {
            RuleOutcome outcome;
            try
            {
                outcome = rule.Evaluate(context);
            }
            catch (Exception ex)
            {
                // One rule with a bug must not cost the user every other recommendation. The failure is
                // reported rather than swallowed so it is visible instead of looking like silence.
                failures.Add($"{rule.Title} could not be evaluated: {ex.Message}");
                continue;
            }

            switch (outcome)
            {
                case RuleOutcome.Applicable applicableOutcome:
                    applicable.Add(applicableOutcome.Recommendation);
                    break;
                case RuleOutcome.NotApplicable notApplicableOutcome:
                    notApplicable.Add(notApplicableOutcome);
                    break;
                case RuleOutcome.Undetermined undeterminedOutcome:
                    undetermined.Add(undeterminedOutcome);
                    break;
            }
        }

        _conflictDetector.Annotate(applicable);

        return new RecommendationReport
        {
            Recommendations = Rank(applicable),
            NotApplicable = notApplicable,
            Undetermined = undetermined,
            Failures = failures,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Ranks by expected value rather than by raw impact: a change that might help a lot but carries
    /// a real cost should not sit above a smaller change that is free. Measured findings outrank
    /// inferred ones at equal impact, because a measurement on this machine beats a generalisation.
    /// </summary>
    private static IReadOnlyList<Recommendation> Rank(List<Recommendation> recommendations)
    {
        return recommendations
            .OrderByDescending(recommendation => recommendation.Confidence == RecommendationConfidence.Measured)
            .ThenByDescending(recommendation => recommendation.ImpactScore * WeightFor(recommendation.Safety.Level))
            .ThenByDescending(recommendation => recommendation.Safety.Score)
            .ThenBy(recommendation => recommendation.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double WeightFor(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => 1.0,
        SafetyLevel.Cautious => 0.9,
        SafetyLevel.Consequential => 0.6,
        _ => 0.4
    };
}
