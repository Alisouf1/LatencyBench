using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// The runner is tested against recording tweaks rather than the real catalog: the behaviour that
/// matters is the ordering and the rollback, and verifying those must not depend on actually
/// changing the power plan of the machine running the tests.
/// </summary>
public class OptimizationRunnerTests
{
    private sealed class RecordingTweak : ITweak
    {
        private readonly List<string> _log;
        private readonly bool _failOnApply;

        public RecordingTweak(string id, List<string> log, bool failOnApply = false, bool failOnRevert = false)
        {
            _log = log;
            _failOnApply = failOnApply;
            FailOnRevert = failOnRevert;
            Definition = new TweakDefinition
            {
                Id = id,
                Category = TweakCategory.Power,
                Name = id,
                Description = id
            };
        }

        public bool FailOnRevert { get; }

        public TweakDefinition Definition { get; }

        public bool IsApplied { get; private set; }

        public TweakState GetState() => IsApplied ? TweakState.Applied : TweakState.NotApplied;

        public void Apply()
        {
            if (_failOnApply)
            {
                _log.Add($"apply-failed:{Definition.Id}");
                throw new InvalidOperationException($"{Definition.Id} refused to apply.");
            }

            _log.Add($"apply:{Definition.Id}");
            IsApplied = true;
        }

        public void Revert()
        {
            if (FailOnRevert)
            {
                _log.Add($"revert-failed:{Definition.Id}");
                throw new InvalidOperationException($"{Definition.Id} refused to revert.");
            }

            _log.Add($"revert:{Definition.Id}");
            IsApplied = false;
        }
    }

    private sealed class FakeCatalog : TweakCatalog
    {
        private readonly IReadOnlyList<ITweak> _tweaks;

        public FakeCatalog(params ITweak[] tweaks) => _tweaks = tweaks;

        public override IReadOnlyList<ITweak> BuildAll() => _tweaks;
    }

    private sealed class NoRestorePointService : RestorePointService
    {
        public int Calls { get; private set; }

        public override (bool Success, string Message) CreateRestorePoint(string description)
        {
            Calls++;
            return (true, "Test restore point.");
        }
    }

    private static PlanStep Step(string tweakId, int order, ExecutionPhase phase = ExecutionPhase.Immediate) => new()
    {
        Order = order,
        Phase = phase,
        Recommendation = new Recommendation
        {
            Id = $"rec.{tweakId}",
            Title = tweakId,
            Category = RecommendationCategory.Power,
            Reasoning = "test",
            ExpectedBenefit = "test",
            Risks = string.Empty,
            Evidence = new[] { new Evidence("test", "test") },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak(tweakId),
            ImpactScore = 50
        }
    };

    private static OptimizationPlan Plan(params PlanStep[] steps) => new()
    {
        Profile = new ProfileCatalog().Get(ProfileKind.Gaming),
        Steps = steps,
        Skipped = Array.Empty<SkippedRecommendation>(),
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Accepts Apply() without error but never actually changes state — the shape of a real tweak
    /// whose registry write succeeds while Group Policy, a vendor utility, or a driver immediately
    /// overrides the value back.
    /// </summary>
    private sealed class SilentlyIneffectiveTweak : ITweak
    {
        public SilentlyIneffectiveTweak(string id) => Definition = new TweakDefinition
        {
            Id = id,
            Category = TweakCategory.Power,
            Name = id,
            Description = id
        };

        public TweakDefinition Definition { get; }

        public TweakState GetState() => TweakState.NotApplied;

        public void Apply()
        {
            // Deliberately does nothing: no exception, no change.
        }

        public void Revert()
        {
        }
    }

    /// <summary>Applies fine but cannot report its own state afterwards.</summary>
    private sealed class UnreadableTweak : ITweak
    {
        public UnreadableTweak(string id) => Definition = new TweakDefinition
        {
            Id = id,
            Category = TweakCategory.Power,
            Name = id,
            Description = id
        };

        public TweakDefinition Definition { get; }

        public TweakState GetState() => TweakState.Unknown;

        public void Apply()
        {
        }

        public void Revert()
        {
        }
    }

    [Fact]
    public async Task ATweakThatSilentlyDoesNotTakeEffectIsReportedAsFailedNotApplied()
    {
        // The regression: a step counted as applied purely because Apply() did not throw, so a write
        // that something else immediately overrode was reported to the user as a completed
        // optimisation while the machine was unchanged.
        var catalog = new FakeCatalog(new SilentlyIneffectiveTweak("ghost"));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("ghost", 1)), createRestorePoint: false);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Applied);
        Assert.Single(result.Failures);
        Assert.Contains("still reads as not applied", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnIneffectiveTweakRollsBackTheStepsBeforeIt()
    {
        // Verification failure has to behave exactly like any other step failure, or a half-applied
        // plan would be left behind whenever a write silently did not stick.
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("one", log),
            new SilentlyIneffectiveTweak("ghost"));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("one", 1), Step("ghost", 2)), createRestorePoint: false);

        Assert.True(result.RolledBack);
        Assert.Equal(new[] { "apply:one", "revert:one" }, log);
    }

    [Fact]
    public async Task ATweakWhoseStateCannotBeReadIsAppliedButSaysSo()
    {
        // Unknown means "could not read it back", not "the write failed". Rolling back here would
        // undo a change that is probably fine, so it succeeds while saying it was not confirmed.
        var catalog = new FakeCatalog(new UnreadableTweak("opaque"));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("opaque", 1)), createRestorePoint: false);

        Assert.True(result.Succeeded);
        Assert.Single(result.Applied);
        Assert.False(result.RolledBack);
        Assert.Contains("could not be read back", result.Results[0].Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppliesEveryStepInPlanOrder()
    {
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("one", log),
            new RecordingTweak("two", log),
            new RecordingTweak("three", log));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("one", 1), Step("two", 2), Step("three", 3)), createRestorePoint: false);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "apply:one", "apply:two", "apply:three" }, log);
        Assert.Equal(3, result.Applied.Count);
    }

    [Fact]
    public async Task RollsBackAppliedStepsInReverseOrderWhenOneFails()
    {
        // Reverse order matters: the power plan switch runs first, so it has to be undone last, after
        // the settings scoped to that plan have been put back.
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("one", log),
            new RecordingTweak("two", log),
            new RecordingTweak("boom", log, failOnApply: true),
            new RecordingTweak("never", log));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(
                Plan(Step("one", 1), Step("two", 2), Step("boom", 3), Step("never", 4)),
                createRestorePoint: false);

        Assert.True(result.RolledBack);
        Assert.False(result.Succeeded);
        Assert.Equal(
            new[] { "apply:one", "apply:two", "apply-failed:boom", "revert:two", "revert:one" },
            log);
    }

    [Fact]
    public async Task RolledBackStepsAreNotReportedAsApplied()
    {
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("one", log),
            new RecordingTweak("boom", log, failOnApply: true));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("one", 1), Step("boom", 2)), createRestorePoint: false);

        Assert.Empty(result.Applied);
        Assert.Contains(result.Results, r => r.Outcome == StepOutcome.RolledBack);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task AStepAfterTheFailureIsNeverAttempted()
    {
        var log = new List<string>();
        var never = new RecordingTweak("never", log);
        var catalog = new FakeCatalog(new RecordingTweak("boom", log, failOnApply: true), never);

        await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("boom", 1), Step("never", 2)), createRestorePoint: false);

        Assert.False(never.IsApplied);
        Assert.DoesNotContain("apply:never", log);
    }

    [Fact]
    public async Task RollbackCanBeDisabledSoLaterStepsStillRun()
    {
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("one", log),
            new RecordingTweak("boom", log, failOnApply: true),
            new RecordingTweak("three", log));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(
                Plan(Step("one", 1), Step("boom", 2), Step("three", 3)),
                createRestorePoint: false,
                rollbackOnFailure: false);

        Assert.False(result.RolledBack);
        Assert.Equal(new[] { "apply:one", "apply-failed:boom", "apply:three" }, log);
        Assert.Equal(2, result.Applied.Count);
    }

    [Fact]
    public async Task AFailedRollbackIsReportedRatherThanHidden()
    {
        // The worst outcome the runner can produce: a change that was applied and could not be undone.
        // It has to be visible, and it has to point the user at the restore point.
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("stuck", log, failOnRevert: true),
            new RecordingTweak("boom", log, failOnApply: true));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("stuck", 1), Step("boom", 2)), createRestorePoint: false);

        var stuck = result.Results.Single(r => r.Step.Title == "stuck");
        Assert.Equal(StepOutcome.Failed, stuck.Outcome);
        Assert.Contains("could not be undone", stuck.Message);
        Assert.Contains("restore point", stuck.Message);
    }

    [Fact]
    public async Task MissingTweakFailsLoudlyInsteadOfSilently()
    {
        var log = new List<string>();
        var catalog = new FakeCatalog(new RecordingTweak("present", log));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(Plan(Step("absent", 1)), createRestorePoint: false);

        Assert.False(result.Succeeded);
        Assert.Contains("not in the tweak catalog", result.Failures.Single().Message);
    }

    [Fact]
    public async Task ManualOnlyStepsAreRefusedRatherThanReportedAsApplied()
    {
        var plan = new OptimizationPlan
        {
            Profile = new ProfileCatalog().Get(ProfileKind.Custom),
            Steps = new[]
            {
                new PlanStep
                {
                    Order = 1,
                    Phase = ExecutionPhase.Immediate,
                    Recommendation = new Recommendation
                    {
                        Id = "rec.manual",
                        Title = "Manual thing",
                        Category = RecommendationCategory.Security,
                        Reasoning = "test",
                        ExpectedBenefit = "test",
                        Risks = "test",
                        Evidence = new[] { new Evidence("test", "test") },
                        Safety = SafetyAssessment.Build(Reversibility.Manual, BlastRadius.SystemWide),
                        Confidence = RecommendationConfidence.Likely,
                        Action = new RecommendedAction.ManualOnly("Do it yourself."),
                        ImpactScore = 50
                    }
                }
            },
            Skipped = Array.Empty<SkippedRecommendation>(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await new OptimizationRunner(new FakeCatalog(), new NoRestorePointService())
            .ApplyAsync(plan, createRestorePoint: false);

        Assert.False(result.Succeeded);
        Assert.Contains("no automatic action", result.Failures.Single().Message);
    }

    [Fact]
    public async Task ARestorePointIsRequestedBeforeApplying()
    {
        var log = new List<string>();
        var restore = new NoRestorePointService();

        var result = await new OptimizationRunner(new FakeCatalog(new RecordingTweak("one", log)), restore)
            .ApplyAsync(Plan(Step("one", 1)), createRestorePoint: true);

        Assert.Equal(1, restore.Calls);
        Assert.True(result.RestorePointCreated);
    }

    [Fact]
    public async Task NoRestorePointIsTakenForAnEmptyPlan()
    {
        var restore = new NoRestorePointService();

        var result = await new OptimizationRunner(new FakeCatalog(), restore)
            .ApplyAsync(Plan(), createRestorePoint: true);

        Assert.Equal(0, restore.Calls);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CancellationMidRunLeavesAppliedStepsAloneAndSkipsTheRest()
    {
        // Cancelling is a user decision, not a failure — undoing what already succeeded would be a
        // change they did not ask for. What must not happen is the remaining steps running anyway, or
        // being dropped without being reported.
        var log = new List<string>();
        using var cancellation = new CancellationTokenSource();

        // Cancels from inside the first tweak's Apply, so the run is genuinely interrupted partway
        // rather than before it starts.
        var first = new CancellingTweak("one", log, cancellation);
        var second = new RecordingTweak("two", log);
        var catalog = new FakeCatalog(first, second);

        var result = await new OptimizationRunner(catalog, new NoRestorePointService())
            .ApplyAsync(
                Plan(Step("one", 1), Step("two", 2)),
                createRestorePoint: false,
                rollbackOnFailure: true,
                progress: null,
                cancellationToken: cancellation.Token);

        Assert.False(result.RolledBack);
        Assert.False(second.IsApplied);
        Assert.Equal(new[] { "apply:one" }, log);

        var applied = Assert.Single(result.Applied);
        Assert.Equal("one", applied.Step.Title);

        var skipped = Assert.Single(result.Results.Where(r => r.Outcome == StepOutcome.Skipped));
        Assert.Equal("two", skipped.Step.Title);
        Assert.Contains("Cancelled", skipped.Message);
    }

    private sealed class CancellingTweak : ITweak
    {
        private readonly List<string> _log;
        private readonly CancellationTokenSource _cancellation;

        public CancellingTweak(string id, List<string> log, CancellationTokenSource cancellation)
        {
            _log = log;
            _cancellation = cancellation;
            Definition = new TweakDefinition
            {
                Id = id,
                Category = TweakCategory.Power,
                Name = id,
                Description = id
            };
        }

        public TweakDefinition Definition { get; }

        public bool IsApplied { get; private set; }

        // Reports its state honestly, like every real tweak: this one genuinely applies before
        // cancelling, so claiming NotApplied afterwards would model a tweak whose write silently did
        // not stick — a different scenario entirely, and one the runner now correctly fails the step
        // for. See SilentlyIneffectiveTweak for that case.
        public TweakState GetState() => IsApplied ? TweakState.Applied : TweakState.NotApplied;

        public void Apply()
        {
            _log.Add($"apply:{Definition.Id}");
            IsApplied = true;
            _cancellation.Cancel();
        }

        public void Revert()
        {
            _log.Add($"revert:{Definition.Id}");
            IsApplied = false;
        }
    }

    [Fact]
    public async Task ProgressIsReportedForEveryStep()
    {
        var log = new List<string>();
        var seen = new List<string>();
        var catalog = new FakeCatalog(new RecordingTweak("one", log), new RecordingTweak("two", log));

        await new OptimizationRunner(catalog, new NoRestorePointService()).ApplyAsync(
            Plan(Step("one", 1), Step("two", 2)),
            createRestorePoint: false,
            rollbackOnFailure: true,
            progress: new Progress<PlanStep>(step =>
            {
                lock (seen)
                {
                    seen.Add(step.Title);
                }
            }));

        // Progress<T> marshals asynchronously, so allow the posted callbacks to drain.
        for (int i = 0; i < 50 && seen.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        lock (seen)
        {
            Assert.Equal(2, seen.Count);
        }
    }

    [Fact]
    public async Task RebootAndDeviceRestartAreReportedFromWhatActuallyApplied()
    {
        var log = new List<string>();
        var catalog = new FakeCatalog(
            new RecordingTweak("now", log),
            new RecordingTweak("later", log));

        var result = await new OptimizationRunner(catalog, new NoRestorePointService()).ApplyAsync(
            Plan(Step("now", 1), Step("later", 2, ExecutionPhase.RequiresReboot)),
            createRestorePoint: false);

        Assert.True(result.RebootRecommended);
        Assert.False(result.DeviceRestartRecommended);
    }

    [Fact]
    public async Task AnEmptyPlanSucceedsWithoutDoingAnything()
    {
        var result = await new OptimizationRunner(new FakeCatalog(), new NoRestorePointService())
            .ApplyAsync(Plan(), createRestorePoint: false);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Results);
        Assert.False(result.RebootRecommended);
    }
}
