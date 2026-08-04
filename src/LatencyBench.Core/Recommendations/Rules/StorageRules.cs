using System.Collections.Generic;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>Disables NTFS last-access timestamp updates.</summary>
public sealed class LastAccessTimestampRule : IRecommendationRule
{
    public string Id => "rec.storage.last-access-timestamps";

    public string Title => "Disable NTFS last-access timestamps";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        if (context.Profile.StorageDevices.Count == 0)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "No storage devices were detected.");
        }

        TweakState state = context.StateOf("storage.disable-last-access-timestamps");
        if (state == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "Last-access timestamp updates are already disabled.");
        }

        if (state == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The last-access timestamp setting could not be read.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Storage,
            Reasoning =
                "NTFS updates a file's last-accessed time on every read by default, which is a metadata " +
                "write triggered by a read. On a drive with many small, frequently-read files — game " +
                "assets, application data — this adds write traffic that has nothing to do with what the " +
                "read actually needed.",
            ExpectedBenefit =
                "Marginally lower storage I/O overhead, mostly noticeable on drives under heavy small-file " +
                "read load. This is a small effect, not a dramatic one — it is offered because it is free " +
                "and reversible, not because it is transformative.",
            Risks =
                "A small number of older backup and file-synchronisation tools use the last-access " +
                "timestamp to decide what changed. Most modern tools use the last-write timestamp instead " +
                "and are unaffected, but if backups behave oddly afterwards, this is worth reverting first.",
            Evidence = new List<Evidence>
            {
                new("Last-access timestamp updates are currently enabled or system-managed.",
                    "fsutil NTFS behaviour"),
                new($"{context.Profile.StorageDevices.Count} storage device(s) detected on this PC.",
                    "Storage enumeration")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SystemWide),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("storage.disable-last-access-timestamps"),
            ImpactScore = 15
        });
    }
}
