using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Recommendations;

/// <summary>
/// Finds recommendations that cannot, or should not, be applied together.
/// <para>
/// Rules are independent by design — each reasons about one thing and knows nothing about the
/// others — which is what keeps them testable, but it also means nothing inside a rule can notice
/// that its advice contradicts another's. That check belongs here, after every rule has spoken.
/// </para>
/// </summary>
public sealed class ConflictDetector
{
    /// <summary>
    /// Pairs that are known to interact, with the explanation shown to the user. Declared as data
    /// rather than as code so adding a rule does not mean editing a chain of if-statements.
    /// </summary>
    private static readonly (string First, string Second, ConflictKind Kind, string Explanation)[] KnownInteractions =
    {
        (
            "rec.system.virtual-machine",
            "rec.interrupts.usb-controller-affinity",
            ConflictKind.MutuallyExclusive,
            "Inside a virtual machine the USB controller is emulated, so pinning its interrupts to a guest " +
            "core steers a device that does not exist in hardware."
        ),
        (
            "rec.system.virtual-machine",
            "rec.interrupts.enable-msi",
            ConflictKind.MutuallyExclusive,
            "MSI mode on a paravirtualised device is decided by the hypervisor, not by this registry value."
        ),
        (
            "rec.power.high-performance-plan",
            "rec.cpu.disable-core-parking",
            ConflictKind.Interferes,
            "Both change settings on the active power plan. Switching plans first means the core parking " +
            "change has to be re-applied to the new plan, so apply the plan change before this one."
        ),
        (
            "rec.power.high-performance-plan",
            "rec.cpu.min-processor-state",
            ConflictKind.Interferes,
            "Both change settings on the active power plan. Switching plans first means the processor state " +
            "change has to be re-applied to the new plan, so apply the plan change before this one."
        ),
        (
            "rec.power.high-performance-plan",
            "rec.power.usb-selective-suspend",
            ConflictKind.Interferes,
            "Both change settings on the active power plan. Switching plans first means the selective " +
            "suspend change has to be re-applied to the new plan, so apply the plan change before this one."
        ),
        (
            "rec.security.memory-integrity",
            "rec.drivers.worst-offender",
            ConflictKind.Interferes,
            "Both affect the same measurement. Change one at a time and re-trace in between, or you will not " +
            "know which one produced the improvement."
        )
    };

    public void Annotate(IReadOnlyList<Recommendation> recommendations)
    {
        var byId = recommendations.ToDictionary(recommendation => recommendation.Id, StringComparer.Ordinal);
        var conflicts = recommendations.ToDictionary(
            recommendation => recommendation.Id,
            _ => new List<RecommendationConflict>(),
            StringComparer.Ordinal);

        foreach (var (first, second, kind, explanation) in KnownInteractions)
        {
            if (byId.ContainsKey(first) && byId.ContainsKey(second))
            {
                conflicts[first].Add(new RecommendationConflict(second, kind, explanation));
                conflicts[second].Add(new RecommendationConflict(first, kind, explanation));
            }
        }

        AddAffinityOverlaps(recommendations, conflicts);
        AddDuplicateTargets(recommendations, conflicts);

        foreach (Recommendation recommendation in recommendations)
        {
            recommendation.Conflicts = conflicts[recommendation.Id];
        }
    }

    /// <summary>
    /// Two devices pinned to the same core defeats the purpose of pinning either: the point is that
    /// the chosen core is not doing anything else, and now it is doing the other device's interrupts.
    /// </summary>
    private static void AddAffinityOverlaps(
        IReadOnlyList<Recommendation> recommendations,
        Dictionary<string, List<RecommendationConflict>> conflicts)
    {
        var affinity = recommendations
            .Where(recommendation => recommendation.Action is RecommendedAction.SetInterruptAffinity)
            .ToList();

        for (int i = 0; i < affinity.Count; i++)
        {
            for (int j = i + 1; j < affinity.Count; j++)
            {
                var left = (RecommendedAction.SetInterruptAffinity)affinity[i].Action;
                var right = (RecommendedAction.SetInterruptAffinity)affinity[j].Action;

                var shared = left.Cores.Intersect(right.Cores).ToList();
                if (shared.Count == 0)
                {
                    continue;
                }

                string explanation =
                    $"Both would send interrupts to logical processor {string.Join(", ", shared)}. Sharing the " +
                    "target core means neither device gets the quiet core the recommendation assumes.";

                conflicts[affinity[i].Id].Add(
                    new RecommendationConflict(affinity[j].Id, ConflictKind.MutuallyExclusive, explanation));
                conflicts[affinity[j].Id].Add(
                    new RecommendationConflict(affinity[i].Id, ConflictKind.MutuallyExclusive, explanation));
            }
        }
    }

    /// <summary>
    /// Catches the case that data-driven pairs cannot: two rules that both ended up wanting to apply
    /// the same tweak. That is a bug rather than a user-facing conflict, but reporting it is better
    /// than applying the same change twice and recording the second backup over the first.
    /// </summary>
    private static void AddDuplicateTargets(
        IReadOnlyList<Recommendation> recommendations,
        Dictionary<string, List<RecommendationConflict>> conflicts)
    {
        var groups = recommendations
            .Where(recommendation => recommendation.Action is RecommendedAction.ApplyTweak)
            .GroupBy(recommendation => ((RecommendedAction.ApplyTweak)recommendation.Action).TweakId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            foreach (Recommendation recommendation in group)
            {
                foreach (Recommendation other in group.Where(candidate => candidate.Id != recommendation.Id))
                {
                    conflicts[recommendation.Id].Add(new RecommendationConflict(
                        other.Id,
                        ConflictKind.MutuallyExclusive,
                        $"Both apply the same underlying change ({group.Key}). Only one should be kept."));
                }
            }
        }
    }
}
