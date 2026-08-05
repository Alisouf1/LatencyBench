using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Guards the property that makes profiles meaningful at all: given the same findings, different
/// profiles must produce different plans.
///
/// The regression these exist for was found by end-to-end execution, not by reading code. There was
/// no Input category in <see cref="RecommendationCategory"/>, so the pointer-acceleration
/// recommendation was filed under System. Productivity accepts System — it wants the timer and
/// scheduler checks — so it silently accepted an input tweak it is explicitly documented to skip,
/// and every one of the six profiles planned exactly the same thing. From the UI that is
/// indistinguishable from a profile selector that does nothing, which is how it was reported.
/// </summary>
public sealed class ProfileDifferentiationTests
{
    private static Recommendation Recommendation(
        string id,
        RecommendationCategory category,
        bool requiresReboot = false,
        SafetyLevel safety = SafetyLevel.Safe) => new()
        {
            Id = id,
            Title = id,
            Category = category,
            Reasoning = "test",
            ExpectedBenefit = "test",
            Risks = string.Empty,
            Evidence = new[] { new Evidence("test", "test") },
            Safety = SafetyAssessment.Build(
                Reversibility.FullyAutomatic,
                safety == SafetyLevel.Safe ? BlastRadius.SingleDevice : BlastRadius.SystemWide),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak(id),
            RequiresReboot = requiresReboot,
            ImpactScore = 50,
        };

    private static RecommendationReport Report(params Recommendation[] recommendations) => new()
    {
        Recommendations = recommendations,
        NotApplicable = Array.Empty<RuleOutcome.NotApplicable>(),
        Undetermined = Array.Empty<RuleOutcome.Undetermined>(),
        Failures = Array.Empty<string>(),
        GeneratedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ProductivityRefusesInputTweaksThatCompetitiveFpsAccepts()
    {
        // The exact bug: with the recommendation categorised as System instead of Input, both of
        // these planned it and the two profiles were indistinguishable.
        var report = Report(Recommendation("rec.input.pointer-precision", RecommendationCategory.Input));
        var planner = new OptimizationPlanner();
        var catalog = new ProfileCatalog();

        OptimizationPlan competitive = planner.Plan(report, catalog.Get(ProfileKind.CompetitiveFps));
        OptimizationPlan productivity = planner.Plan(report, catalog.Get(ProfileKind.Productivity));

        Assert.Single(competitive.Steps);
        Assert.Empty(productivity.Steps);
        Assert.Single(productivity.Skipped);
        Assert.Contains("Input", productivity.Skipped[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRecommendationCategoryIsAcceptedBySomeProfile()
    {
        // A category no profile accepts would make its recommendations permanently unreachable
        // through the Optimize tab - findable by the analyser but impossible to apply.
        var catalog = new ProfileCatalog();
        IReadOnlyList<OptimizationProfile> profiles = catalog.BuildAll();

        foreach (RecommendationCategory category in Enum.GetValues<RecommendationCategory>())
        {
            Assert.True(
                profiles.Any(profile => profile.Categories.Contains(category)),
                $"No profile accepts {category}, so those recommendations could never be applied.");
        }
    }

    [Fact]
    public void TheThreeHeadlineProfilesDoNotAllProduceIdenticalPlans()
    {
        // Balanced/Gaming/Competitive are the three the user picks between, so they must genuinely
        // differ over a realistic mixed report rather than only in their descriptions.
        var report = Report(
            Recommendation("rec.input.pointer-precision", RecommendationCategory.Input),
            Recommendation("rec.cpu.min-processor-state", RecommendationCategory.Cpu),
            Recommendation("rec.network.nagle", RecommendationCategory.Network),
            Recommendation("rec.interrupts.enable-msi", RecommendationCategory.Interrupts, requiresReboot: true));

        var planner = new OptimizationPlanner();
        var catalog = new ProfileCatalog();

        string[] balanced = planner.Plan(report, catalog.Get(ProfileKind.Balanced))
            .Steps.Select(s => s.Recommendation.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        string[] gaming = planner.Plan(report, catalog.Get(ProfileKind.Gaming))
            .Steps.Select(s => s.Recommendation.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        // Balanced refuses reboot-required work and the processor-state power cost; Gaming accepts
        // both but declines Nagle. If these ever match, profile filtering has stopped working.
        Assert.NotEqual(balanced, gaming);
        Assert.DoesNotContain("rec.interrupts.enable-msi", balanced);
        Assert.Contains("rec.interrupts.enable-msi", gaming);
        Assert.DoesNotContain("rec.network.nagle", gaming);
    }

    [Fact]
    public void BalancedRefusesRebootRequiredWorkThatGamingAccepts()
    {
        var report = Report(
            Recommendation("rec.needs-reboot", RecommendationCategory.Cpu, requiresReboot: true));

        var planner = new OptimizationPlanner();
        var catalog = new ProfileCatalog();

        Assert.Empty(planner.Plan(report, catalog.Get(ProfileKind.Balanced)).Steps);
        Assert.Single(planner.Plan(report, catalog.Get(ProfileKind.Gaming)).Steps);
    }
}
