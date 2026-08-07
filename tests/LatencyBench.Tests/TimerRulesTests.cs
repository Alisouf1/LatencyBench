using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Recommendations.Rules;
using LatencyBench.Core.Timers;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the two timer rules. Both read boot configuration that can only be obtained by shelling out
/// to bcdedit as administrator, so the state they reason about is frequently unavailable - and the
/// distinction they have to preserve is between "read it, nothing is set" and "could not read it".
/// Collapsing those two would make an unelevated run quietly report a clean machine.
/// </summary>
public sealed class TimerRulesTests
{
    private static TimerState Timers(
        BcdFlagState usePlatformClock = BcdFlagState.NotSet,
        BcdFlagState usePlatformTick = BcdFlagState.NotSet,
        BcdFlagState disableDynamicTick = BcdFlagState.NotSet,
        bool perProcessResolution = true,
        bool globalRequested = false,
        double currentMs = 15.625,
        double coarsestMs = 15.625,
        double finestMs = 0.5,
        string? bootError = null) => new()
        {
            CoarsestMs = coarsestMs,
            FinestMs = finestMs,
            CurrentMs = currentMs,
            UsesPerProcessTimerResolution = perProcessResolution,
            GlobalTimerResolutionRequested = globalRequested,
            UsePlatformClock = usePlatformClock,
            UsePlatformTick = usePlatformTick,
            DisableDynamicTick = disableDynamicTick,
            BootConfigurationError = bootError,
        };

    private static RecommendationContext Context(
        TimerState? timers = null,
        Dictionary<string, TweakState>? tweakStates = null,
        bool hasBattery = false,
        int build = 26100) => new()
        {
            Profile = TestProfiles.Profile(
                windows: TestProfiles.Windows(build: build),
                hasBattery: hasBattery),
            TweakStates = tweakStates ?? TestProfiles.AllNotApplied(),
            Timers = timers,
            IsElevated = true,
        };

    // ============================================================================================
    // PlatformClockRule
    // ============================================================================================

    [Fact]
    public void PlatformClockIsUndeterminedWhenTimerStateWasNeverRead()
    {
        var outcome = new PlatformClockRule().Evaluate(Context(timers: null));

        var undetermined = Assert.IsType<RuleOutcome.Undetermined>(outcome);
        Assert.Equal("rec.system.platform-clock", undetermined.RuleId);
    }

    [Fact]
    public void PlatformClockIsUndeterminedWhenBcdeditCouldNotBeRead()
    {
        // The important one: unelevated, bcdedit exits non-zero, and the flag is Unknown. Reporting
        // "no forced clock" here would tell the user their machine is fine on the strength of a read
        // that never happened.
        var outcome = new PlatformClockRule().Evaluate(
            Context(Timers(usePlatformClock: BcdFlagState.Unknown)));

        var undetermined = Assert.IsType<RuleOutcome.Undetermined>(outcome);
        Assert.Contains("administrator", undetermined.MissingInformation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlatformClockSurfacesTheUnderlyingBcdeditErrorWhenThereIsOne()
    {
        var outcome = new PlatformClockRule().Evaluate(Context(Timers(
            usePlatformClock: BcdFlagState.Unknown,
            bootError: "The boot configuration data store could not be opened.")));

        var undetermined = Assert.IsType<RuleOutcome.Undetermined>(outcome);
        Assert.Contains("could not be opened", undetermined.MissingInformation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BcdFlagState.NotSet)]
    [InlineData(BcdFlagState.Off)]
    public void PlatformClockIsNotApplicableWhenNoClockIsForced(BcdFlagState state)
    {
        // NotSet and Off are different facts about the boot store but the same verdict: Windows is
        // choosing its own clock source, which is what should happen.
        var outcome = new PlatformClockRule().Evaluate(Context(Timers(usePlatformClock: state)));

        var notApplicable = Assert.IsType<RuleOutcome.NotApplicable>(outcome);
        Assert.Contains("which is correct", notApplicable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AForcedPlatformClockIsRecommendedForRemoval()
    {
        var outcome = new PlatformClockRule().Evaluate(Context(Timers(usePlatformClock: BcdFlagState.On)));

        var applicable = Assert.IsType<RuleOutcome.Applicable>(outcome);
        Assert.Equal("rec.system.platform-clock", applicable.Recommendation.Id);
        Assert.True(applicable.Recommendation.RequiresReboot);
        Assert.Equal(95, applicable.Recommendation.ImpactScore);
    }

    [Fact]
    public void TheForcedClockFixIsManualBecauseBootConfigurationIsNotSafeToAutomate()
    {
        // A mistake writing the boot store costs a boot, not a setting. This must never become an
        // ApplyTweak without a deliberate decision.
        var outcome = new PlatformClockRule().Evaluate(Context(Timers(usePlatformClock: BcdFlagState.On)));

        var applicable = Assert.IsType<RuleOutcome.Applicable>(outcome);
        var manual = Assert.IsType<RecommendedAction.ManualOnly>(applicable.Recommendation.Action);
        Assert.Contains("bcdedit /deletevalue useplatformclock", manual.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEvidenceReportsWhetherUsePlatformTickIsAlsoSet()
    {
        var withTick = new PlatformClockRule().Evaluate(Context(Timers(
            usePlatformClock: BcdFlagState.On, usePlatformTick: BcdFlagState.On)));
        var withoutTick = new PlatformClockRule().Evaluate(Context(Timers(
            usePlatformClock: BcdFlagState.On, usePlatformTick: BcdFlagState.NotSet)));

        var withEvidence = Assert.IsType<RuleOutcome.Applicable>(withTick)
            .Recommendation.Evidence.Select(e => e.Observation).ToList();
        var withoutEvidence = Assert.IsType<RuleOutcome.Applicable>(withoutTick)
            .Recommendation.Evidence.Select(e => e.Observation).ToList();

        Assert.Contains(withEvidence, e => e.Contains("useplatformtick is also set", StringComparison.Ordinal));
        Assert.Contains(withoutEvidence, e => e.Contains("useplatformtick is not set", StringComparison.Ordinal));
    }

    // ============================================================================================
    // TimerResolutionRule
    // ============================================================================================

    [Fact]
    public void TimerResolutionIsUndeterminedWhenTimerStateWasNeverRead()
    {
        var outcome = new TimerResolutionRule().Evaluate(Context(timers: null));

        Assert.IsType<RuleOutcome.Undetermined>(outcome);
    }

    [Fact]
    public void TimerResolutionIsNotApplicableOnBuildsWhereRequestsAreStillMachineWide()
    {
        // Before Windows 10 2004 a resolution request already applied system-wide, so there is
        // nothing to restore.
        var outcome = new TimerResolutionRule().Evaluate(
            Context(Timers(perProcessResolution: false), build: 18363));

        var notApplicable = Assert.IsType<RuleOutcome.NotApplicable>(outcome);
        Assert.Contains("nothing to restore", notApplicable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TimerResolutionIsNotApplicableWhenTheRegistryValueIsAlreadySet()
    {
        var outcome = new TimerResolutionRule().Evaluate(Context(Timers(globalRequested: true)));

        var notApplicable = Assert.IsType<RuleOutcome.NotApplicable>(outcome);
        Assert.Contains("already restored", notApplicable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TimerResolutionIsNotApplicableWhenTheTweakItselfReportsApplied()
    {
        // Two independent signals for the same fact - the registry read and the tweak's own state -
        // and either being true has to suppress the recommendation, or it reappears after being applied.
        var states = TestProfiles.AllNotApplied();
        states["timer.global-resolution-requests"] = TweakState.Applied;

        var outcome = new TimerResolutionRule().Evaluate(
            Context(Timers(globalRequested: false), tweakStates: states));

        Assert.IsType<RuleOutcome.NotApplicable>(outcome);
    }

    [Fact]
    public void TimerResolutionIsRecommendedOnAPerProcessBuildWithTheValueUnset()
    {
        var outcome = new TimerResolutionRule().Evaluate(Context(Timers()));

        var applicable = Assert.IsType<RuleOutcome.Applicable>(outcome);
        Assert.Equal("rec.system.timer-resolution", applicable.Recommendation.Id);
        Assert.True(applicable.Recommendation.RequiresReboot);

        var apply = Assert.IsType<RecommendedAction.ApplyTweak>(applicable.Recommendation.Action);
        Assert.Equal("timer.global-resolution-requests", apply.TweakId);
    }

    [Fact]
    public void TimerResolutionIsRatedLowerOnABatteryMachine()
    {
        // A fine system-wide tick wakes cores more often, which is a real battery cost. The rule is
        // supposed to weigh that, and this is the only place that weighting is expressed.
        var desktop = new TimerResolutionRule().Evaluate(Context(Timers(), hasBattery: false));
        var laptop = new TimerResolutionRule().Evaluate(Context(Timers(), hasBattery: true));

        int desktopScore = Assert.IsType<RuleOutcome.Applicable>(desktop).Recommendation.ImpactScore;
        int laptopScore = Assert.IsType<RuleOutcome.Applicable>(laptop).Recommendation.ImpactScore;

        Assert.True(laptopScore < desktopScore,
            $"battery machine scored {laptopScore}, desktop {desktopScore} - the battery cost is not being weighed");
    }

    [Fact]
    public void TheRecommendationIsMarkedInferredBecauseItsBenefitDependsOnOtherSoftware()
    {
        // Whether this helps depends entirely on whether anything the user runs requests a fine
        // timer. Claiming a measured or even likely benefit would be overstating it.
        var outcome = new TimerResolutionRule().Evaluate(Context(Timers()));

        var applicable = Assert.IsType<RuleOutcome.Applicable>(outcome);
        Assert.Equal(RecommendationConfidence.Inferred, applicable.Recommendation.Confidence);
    }

    [Fact]
    public void TheEvidenceReportsWhetherTheTimerIsCurrentlyRaised()
    {
        var raised = new TimerResolutionRule().Evaluate(
            Context(Timers(currentMs: 0.5, coarsestMs: 15.625)));
        var idle = new TimerResolutionRule().Evaluate(
            Context(Timers(currentMs: 15.625, coarsestMs: 15.625)));

        var raisedEvidence = Assert.IsType<RuleOutcome.Applicable>(raised)
            .Recommendation.Evidence.Select(e => e.Observation).ToList();
        var idleEvidence = Assert.IsType<RuleOutcome.Applicable>(idle)
            .Recommendation.Evidence.Select(e => e.Observation).ToList();

        Assert.Contains(raisedEvidence, e => e.Contains("already raised", StringComparison.Ordinal));
        Assert.Contains(idleEvidence, e => e.Contains("idle default", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEvidenceCitesTheBuildNumberThatChangedTheBehaviour()
    {
        var outcome = new TimerResolutionRule().Evaluate(Context(Timers(), build: 22631));

        var evidence = Assert.IsType<RuleOutcome.Applicable>(outcome)
            .Recommendation.Evidence.Select(e => e.Observation).ToList();

        Assert.Contains(evidence, e => e.Contains("22631", StringComparison.Ordinal)
                                       && e.Contains("19041", StringComparison.Ordinal));
    }

    [Fact]
    public void BothRulesReportTheSameIdInEveryOutcomeTheyReturn()
    {
        // The engine keys on RuleId to correlate outcomes across runs; an id that varies by branch
        // would make a rule look like it appeared and disappeared.
        var clock = new PlatformClockRule();
        var resolution = new TimerResolutionRule();

        var clockOutcomes = new RuleOutcome[]
        {
            clock.Evaluate(Context(timers: null)),
            clock.Evaluate(Context(Timers(usePlatformClock: BcdFlagState.Unknown))),
            clock.Evaluate(Context(Timers(usePlatformClock: BcdFlagState.Off))),
            clock.Evaluate(Context(Timers(usePlatformClock: BcdFlagState.On))),
        };

        var resolutionOutcomes = new RuleOutcome[]
        {
            resolution.Evaluate(Context(timers: null)),
            resolution.Evaluate(Context(Timers(perProcessResolution: false))),
            resolution.Evaluate(Context(Timers(globalRequested: true))),
            resolution.Evaluate(Context(Timers())),
        };

        Assert.All(clockOutcomes, o => Assert.Equal(clock.Id, IdOf(o)));
        Assert.All(resolutionOutcomes, o => Assert.Equal(resolution.Id, IdOf(o)));
    }

    private static string IdOf(RuleOutcome outcome) => outcome switch
    {
        RuleOutcome.Applicable a => a.Recommendation.Id,
        RuleOutcome.NotApplicable n => n.RuleId,
        RuleOutcome.Undetermined u => u.RuleId,
        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}"),
    };
}
