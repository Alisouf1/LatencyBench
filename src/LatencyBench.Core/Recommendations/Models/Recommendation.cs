using System;
using System.Collections.Generic;

namespace LatencyBench.Core.Recommendations.Models;

public enum RecommendationCategory
{
    Power,
    Cpu,
    Interrupts,
    Gpu,
    Storage,
    Network,
    Usb,
    Security,
    Drivers,
    System
}

/// <summary>How strongly the evidence supports acting.</summary>
public enum RecommendationConfidence
{
    /// <summary>Inferred from configuration alone — no measurement on this machine backs it up.</summary>
    Inferred,

    /// <summary>Backed by a documented Windows behaviour that applies to this exact configuration.</summary>
    Likely,

    /// <summary>Backed by a measurement taken on this machine.</summary>
    Measured
}

/// <summary>
/// A single fact from this machine that a recommendation rests on. Kept separate from the prose so
/// the UI can show the user what was actually observed rather than asking them to trust a sentence.
/// </summary>
public sealed record Evidence(string Observation, string Source);

/// <summary>What applying a recommendation actually does.</summary>
public abstract record RecommendedAction
{
    /// <summary>Applies one of the tweaks from <see cref="Tweaks.TweakCatalog"/>.</summary>
    public sealed record ApplyTweak(string TweakId) : RecommendedAction;

    /// <summary>Pins a device's interrupts to specific logical processors.</summary>
    public sealed record SetInterruptAffinity(string InstanceId, IReadOnlyList<int> Cores) : RecommendedAction;

    /// <summary>Turns message-signalled interrupts on for a device.</summary>
    public sealed record EnableMsiMode(string InstanceId) : RecommendedAction;

    /// <summary>
    /// Nothing LatencyBench will do on the user's behalf. Used where the change is outside what this
    /// app should touch — a firmware setting, or something with a security cost the user must weigh
    /// themselves — so the recommendation still explains itself without offering a button.
    /// </summary>
    public sealed record ManualOnly(string Instructions) : RecommendedAction;
}

/// <summary>
/// One suggestion, with everything needed to justify it. Every field below the identity exists
/// because a recommendation the user cannot interrogate is indistinguishable from folklore.
/// </summary>
public sealed class Recommendation
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required RecommendationCategory Category { get; init; }

    /// <summary>Why this is being suggested for this machine specifically.</summary>
    public required string Reasoning { get; init; }

    /// <summary>What the user should expect to change, stated without overpromising.</summary>
    public required string ExpectedBenefit { get; init; }

    /// <summary>What could go wrong, or the empty string when genuinely nothing can.</summary>
    public required string Risks { get; init; }

    /// <summary>The observations this rests on.</summary>
    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public required SafetyAssessment Safety { get; init; }

    public required RecommendationConfidence Confidence { get; init; }

    public required RecommendedAction Action { get; init; }

    public bool RequiresReboot { get; init; }

    /// <summary>
    /// Estimated impact on latency, 0-100, used only for ordering. Deliberately coarse: the honest
    /// claim is "this one matters more than that one on this machine", not a millisecond figure.
    /// </summary>
    public required int ImpactScore { get; init; }

    /// <summary>Conflicts found against other recommendations in the same run.</summary>
    public IReadOnlyList<RecommendationConflict> Conflicts { get; set; } = Array.Empty<RecommendationConflict>();

    public bool HasBlockingConflict
    {
        get
        {
            foreach (var conflict in Conflicts)
            {
                if (conflict.Kind == ConflictKind.MutuallyExclusive)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

public enum ConflictKind
{
    /// <summary>Both cannot be applied — one undoes the other.</summary>
    MutuallyExclusive,

    /// <summary>Both can be applied, but the combination weakens or complicates one of them.</summary>
    Interferes
}

public sealed record RecommendationConflict(string OtherRecommendationId, ConflictKind Kind, string Explanation);

/// <summary>
/// A rule's verdict. Rules report why they did NOT fire as well as why they did — a user asking
/// "why isn't it telling me to enable MSI on my GPU" deserves an answer, and silence is the
/// behaviour that makes a recommendation engine feel arbitrary.
/// </summary>
public abstract record RuleOutcome
{
    public sealed record Applicable(Recommendation Recommendation) : RuleOutcome;

    /// <summary>The rule examined this machine and decided no action is warranted.</summary>
    public sealed record NotApplicable(string RuleId, string Title, string Reason) : RuleOutcome;

    /// <summary>The rule could not reach a verdict because the information it needs is missing.</summary>
    public sealed record Undetermined(string RuleId, string Title, string MissingInformation) : RuleOutcome;
}
