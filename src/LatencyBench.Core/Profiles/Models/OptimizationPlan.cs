using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Profiles.Models;

/// <summary>
/// The order steps must run in. This is correctness, not presentation.
/// <para>
/// The power plan has to change before anything scoped to a power plan, because switching plans
/// moves those settings to a different scheme and a value written to the old one is simply not in
/// effect afterwards. Everything else is ordered so the disruptive steps come last: a user who
/// stops partway through has still had the harmless changes applied.
/// </para>
/// </summary>
public enum ExecutionPhase
{
	/// <summary>Switch the active power plan. Must be first.</summary>
	PowerPlan = 0,

	/// <summary>Settings stored against the active power plan.</summary>
	PowerPlanScoped = 1,

	/// <summary>Registry and device settings that take effect immediately.</summary>
	Immediate = 2,

	/// <summary>Changes that need a device restart — these briefly disconnect hardware.</summary>
	DeviceRestart = 3,

	/// <summary>Changes that do nothing until the machine is restarted.</summary>
	RequiresReboot = 4
}

public sealed class PlanStep
{
	public required Recommendation Recommendation { get; init; }

	public required ExecutionPhase Phase { get; init; }

	/// <summary>Position within the whole plan, for display. Assigned by the planner.</summary>
	public required int Order { get; init; }

	public string Title => Recommendation.Title;
}

/// <summary>A recommendation the profile declined, and why.</summary>
public sealed record SkippedRecommendation(Recommendation Recommendation, string Reason);

/// <summary>
/// What a profile would do to this machine, before anything is changed. Produced separately from
/// execution so the user can read the whole thing first.
/// </summary>
public sealed class OptimizationPlan
{
	public required OptimizationProfile Profile { get; init; }

	public required IReadOnlyList<PlanStep> Steps { get; init; }

	/// <summary>Recommendations this profile declined, each with the reason.</summary>
	public required IReadOnlyList<SkippedRecommendation> Skipped { get; init; }

	public required DateTimeOffset CreatedAt { get; init; }

	public bool IsEmpty => Steps.Count == 0;

	public bool RequiresReboot => Steps.Any(step => step.Phase == ExecutionPhase.RequiresReboot);

	public bool RequiresDeviceRestart => Steps.Any(step => step.Phase == ExecutionPhase.DeviceRestart);

	/// <summary>The lowest safety score among the steps — the plan is only as safe as its worst step.</summary>
	public int LowestSafetyScore => Steps.Count == 0
		? 100
		: Steps.Min(step => step.Recommendation.Safety.Score);
}

public enum StepOutcome
{
	Applied,
	Failed,
	RolledBack,
	Skipped
}

public sealed record StepResult(PlanStep Step, StepOutcome Outcome, string? Message = null);

/// <summary>The result of executing a plan.</summary>
public sealed class OptimizationResult
{
	public required OptimizationPlan Plan { get; init; }

	public required IReadOnlyList<StepResult> Results { get; init; }

	/// <summary>Whether a System Restore point was created, and what happened if not.</summary>
	public required string RestorePointMessage { get; init; }

	public required bool RestorePointCreated { get; init; }

	/// <summary>True when a failure caused already-applied steps to be undone.</summary>
	public required bool RolledBack { get; init; }

	public required DateTimeOffset CompletedAt { get; init; }

	public IReadOnlyList<StepResult> Applied =>
		Results.Where(result => result.Outcome == StepOutcome.Applied).ToList();

	public IReadOnlyList<StepResult> Failures =>
		Results.Where(result => result.Outcome == StepOutcome.Failed).ToList();

	public bool Succeeded => Failures.Count == 0 && !RolledBack;

	public bool RebootRecommended =>
		Applied.Any(result => result.Step.Phase == ExecutionPhase.RequiresReboot);

	public bool DeviceRestartRecommended =>
		Applied.Any(result => result.Step.Phase == ExecutionPhase.DeviceRestart);
}
