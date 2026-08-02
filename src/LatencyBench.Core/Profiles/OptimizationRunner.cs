using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Core.Profiles;

/// <summary>
/// Executes a plan.
/// <para>
/// The contract is all-or-nothing by default: if any step fails, every step already applied in the
/// same run is undone, in reverse order. A half-applied profile is the worst outcome available —
/// the machine is in a state neither the user nor the app chose, and the user has no way to know
/// which half took effect.
/// </para>
/// </summary>
public sealed class OptimizationRunner
{
    private readonly TweakCatalog _tweakCatalog;
    private readonly RestorePointService _restorePointService;
    private readonly InterruptAffinityService? _affinityService;
    private readonly InterruptDeviceService? _interruptDeviceService;

    public OptimizationRunner(
        TweakCatalog? tweakCatalog = null,
        RestorePointService? restorePointService = null,
        InterruptAffinityService? affinityService = null,
        InterruptDeviceService? interruptDeviceService = null)
    {
        _tweakCatalog = tweakCatalog ?? new TweakCatalog();
        _restorePointService = restorePointService ?? new RestorePointService();
        _affinityService = affinityService;
        _interruptDeviceService = interruptDeviceService;
    }

    /// <summary>
    /// Applies the plan. Progress is reported per step so a long run is not silent.
    /// </summary>
    public Task<OptimizationResult> ApplyAsync(
        OptimizationPlan plan,
        bool createRestorePoint = true,
        bool rollbackOnFailure = true,
        IProgress<PlanStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Apply(plan, createRestorePoint, rollbackOnFailure, progress, cancellationToken),
            cancellationToken);
    }

    private OptimizationResult Apply(
        OptimizationPlan plan,
        bool createRestorePoint,
        bool rollbackOnFailure,
        IProgress<PlanStep>? progress,
        CancellationToken cancellationToken)
    {
        // Resolved once. Building the catalog enumerates the USB tree and the network adapters, and
        // doing that per step would make a ten-step plan re-walk the device tree ten times.
        Dictionary<string, ITweak> tweaks = _tweakCatalog.BuildAll()
            .ToDictionary(tweak => tweak.Definition.Id, StringComparer.Ordinal);

        bool restorePointCreated = false;
        string restorePointMessage = "Not requested.";

        if (createRestorePoint && !plan.IsEmpty)
        {
            (restorePointCreated, restorePointMessage) = _restorePointService.CreateRestorePoint(
                $"LatencyBench — before applying the {plan.Profile.Name} profile");
        }

        var results = new List<StepResult>(plan.Steps.Count);
        var appliedSteps = new List<PlanStep>();
        bool rolledBack = false;

        foreach (PlanStep step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is not a failure, so what has already been applied stays applied. The
                // remaining steps are reported as skipped rather than silently dropped.
                results.Add(new StepResult(step, StepOutcome.Skipped, "Cancelled before this step ran."));
                continue;
            }

            progress?.Report(step);

            try
            {
                Execute(step, tweaks);
                appliedSteps.Add(step);
                results.Add(new StepResult(step, StepOutcome.Applied));
            }
            catch (Exception ex)
            {
                results.Add(new StepResult(step, StepOutcome.Failed, ex.Message));

                if (!rollbackOnFailure)
                {
                    continue;
                }

                rolledBack = true;
                RollBack(appliedSteps, tweaks, results);
                break;
            }
        }

        return new OptimizationResult
        {
            Plan = plan,
            Results = results,
            RestorePointCreated = restorePointCreated,
            RestorePointMessage = restorePointMessage,
            RolledBack = rolledBack,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private void Execute(PlanStep step, IReadOnlyDictionary<string, ITweak> tweaks)
    {
        switch (step.Recommendation.Action)
        {
            case RecommendedAction.ApplyTweak apply:
                if (!tweaks.TryGetValue(apply.TweakId, out ITweak? tweak))
                {
                    throw new InvalidOperationException(
                        $"'{apply.TweakId}' is not in the tweak catalog, so it cannot be applied.");
                }

                tweak.Apply();
                break;

            case RecommendedAction.SetInterruptAffinity affinity:
                if (_affinityService is null)
                {
                    throw new InvalidOperationException(
                        "Interrupt affinity changes are not available in this run.");
                }

                _affinityService.SetSpecifiedCores(affinity.InstanceId, affinity.Cores);
                break;

            case RecommendedAction.EnableMsiMode msi:
                if (_interruptDeviceService is null)
                {
                    throw new InvalidOperationException("MSI changes are not available in this run.");
                }

                _interruptDeviceService.SetMsiMode(msi.InstanceId, enabled: true);
                break;

            case RecommendedAction.ManualOnly:
                // The planner already refuses these, so reaching here means a caller built a plan by
                // hand. Failing loudly is better than silently reporting a manual instruction as applied.
                throw new InvalidOperationException(
                    $"'{step.Title}' has no automatic action and cannot be applied.");

            default:
                throw new InvalidOperationException($"Unsupported action for '{step.Title}'.");
        }
    }

    /// <summary>
    /// Undoes what this run applied, newest first.
    /// <para>
    /// Reverse order matters for the same reason the forward order did: the power plan switch runs
    /// first, so it has to be undone last, after the settings scoped to that plan have been put back.
    /// </para>
    /// </summary>
    private void RollBack(
        IReadOnlyList<PlanStep> appliedSteps,
        IReadOnlyDictionary<string, ITweak> tweaks,
        List<StepResult> results)
    {
        for (int i = appliedSteps.Count - 1; i >= 0; i--)
        {
            PlanStep step = appliedSteps[i];

            // The result recorded when this step succeeded is replaced, so the final report shows what
            // the machine actually ended up with rather than a step marked Applied that was undone.
            int existingIndex = results.FindIndex(result => ReferenceEquals(result.Step, step));

            try
            {
                Revert(step, tweaks);
                var rolledBack = new StepResult(step, StepOutcome.RolledBack, "Undone after a later step failed.");
                if (existingIndex >= 0)
                {
                    results[existingIndex] = rolledBack;
                }
                else
                {
                    results.Add(rolledBack);
                }
            }
            catch (Exception ex)
            {
                // A failed rollback is the most serious outcome this class can produce, so it is
                // reported explicitly rather than folded into the general failure.
                var failed = new StepResult(
                    step,
                    StepOutcome.Failed,
                    $"Applied, but could not be undone automatically: {ex.Message}. " +
                    "Revert it from the Tweaks tab, or use the restore point created before this run.");

                if (existingIndex >= 0)
                {
                    results[existingIndex] = failed;
                }
                else
                {
                    results.Add(failed);
                }
            }
        }
    }

    private void Revert(PlanStep step, IReadOnlyDictionary<string, ITweak> tweaks)
    {
        switch (step.Recommendation.Action)
        {
            case RecommendedAction.ApplyTweak apply when tweaks.TryGetValue(apply.TweakId, out ITweak? tweak):
                tweak.Revert();
                break;

            case RecommendedAction.SetInterruptAffinity affinity when _affinityService is not null:
                _affinityService.ClearOverride(affinity.InstanceId);
                break;

            case RecommendedAction.EnableMsiMode msi when _interruptDeviceService is not null:
                _interruptDeviceService.SetMsiMode(msi.InstanceId, enabled: false);
                break;

            default:
                throw new InvalidOperationException("No way to undo this step automatically.");
        }
    }
}
