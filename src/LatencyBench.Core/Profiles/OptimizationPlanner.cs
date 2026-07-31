using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Profiles;

/// <summary>
/// Turns a recommendation report plus a profile into an ordered plan.
/// <para>
/// Nothing here decides what is a good idea — that was the engine's job. This decides which of the
/// engine's findings a given profile accepts, and what order they have to run in.
/// </para>
/// </summary>
public sealed class OptimizationPlanner
{
	/// <summary>
	/// Recommendations whose underlying setting is stored against the active power plan. Switching
	/// plans re-scopes them, so they must run after the plan change or the value lands on a scheme
	/// that is no longer active.
	/// </summary>
	private static readonly HashSet<string> PowerPlanScopedIds = new(StringComparer.Ordinal)
	{
		"rec.cpu.min-processor-state",
		"rec.cpu.disable-core-parking",
		"rec.power.usb-selective-suspend"
	};

	private const string PowerPlanSwitchId = "rec.power.high-performance-plan";

	public OptimizationPlan Plan(RecommendationReport report, OptimizationProfile profile)
	{
		var accepted = new List<Recommendation>();
		var skipped = new List<SkippedRecommendation>();

		foreach (Recommendation recommendation in report.Recommendations)
		{
			string? reason = RejectionReason(recommendation, profile);
			if (reason is null)
			{
				accepted.Add(recommendation);
			}
			else
			{
				skipped.Add(new SkippedRecommendation(recommendation, reason));
			}
		}

		var steps = accepted
			.Select(recommendation => (recommendation, phase: PhaseFor(recommendation)))
			.OrderBy(pair => pair.phase)
			// Inside a phase, put what the profile exists for first, then the biggest effect.
			.ThenByDescending(pair => profile.Priorities.Contains(pair.recommendation.Id))
			.ThenByDescending(pair => pair.recommendation.ImpactScore)
			.ThenBy(pair => pair.recommendation.Title, StringComparer.OrdinalIgnoreCase)
			.Select((pair, index) => new PlanStep
			{
				Recommendation = pair.recommendation,
				Phase = pair.phase,
				Order = index + 1
			})
			.ToList();

		return new OptimizationPlan
		{
			Profile = profile,
			Steps = steps,
			Skipped = skipped,
			CreatedAt = DateTimeOffset.UtcNow
		};
	}

	/// <summary>Null when the profile accepts the recommendation; otherwise the reason it does not.</summary>
	private static string? RejectionReason(Recommendation recommendation, OptimizationProfile profile)
	{
		// A hard blocker outranks every profile setting, including Custom's willingness to accept
		// anything. Turning off a security feature or making an irreversible change is only ever done
		// by the user picking it individually, never by running a profile.
		if (recommendation.Safety.Blockers.Count > 0)
		{
			return recommendation.Safety.Blockers[0];
		}

		if (profile.Exclusions.TryGetValue(recommendation.Id, out string? exclusion))
		{
			return exclusion;
		}

		if (recommendation.HasBlockingConflict)
		{
			RecommendationConflict conflict = recommendation.Conflicts
				.First(candidate => candidate.Kind == ConflictKind.MutuallyExclusive);
			return $"Conflicts with another recommendation: {conflict.Explanation}";
		}

		if (recommendation.Action is RecommendedAction.ManualOnly)
		{
			return "This one has no automatic action — it explains something you have to do yourself.";
		}

		if (!profile.Categories.Contains(recommendation.Category))
		{
			return $"The {profile.Name} profile does not change {recommendation.Category} settings.";
		}

		if (recommendation.Safety.Level > profile.MaximumSafetyLevel)
		{
			return $"Rated {recommendation.Safety.Level}, which is beyond what the {profile.Name} profile " +
				   $"applies without being asked (limit: {profile.MaximumSafetyLevel}).";
		}

		if (recommendation.RequiresReboot && !profile.AcceptsRebootRequired)
		{
			return $"Needs a restart, and the {profile.Name} profile only applies changes that take effect immediately.";
		}

		if (!profile.AcceptsPowerCost && HasPowerCost(recommendation))
		{
			return $"Trades power draw and heat for latency, which the {profile.Name} profile does not do.";
		}

		return null;
	}

	/// <summary>
	/// Whether the main cost of a recommendation is power. Decided from the category rather than by
	/// matching on ids, so a new power or CPU rule inherits the behaviour without this needing an edit.
	/// </summary>
	private static bool HasPowerCost(Recommendation recommendation) =>
		recommendation.Category is RecommendationCategory.Power or RecommendationCategory.Cpu;

	private static ExecutionPhase PhaseFor(Recommendation recommendation)
	{
		if (recommendation.Id == PowerPlanSwitchId)
		{
			return ExecutionPhase.PowerPlan;
		}

		if (recommendation.RequiresReboot)
		{
			return ExecutionPhase.RequiresReboot;
		}

		if (recommendation.Action is RecommendedAction.SetInterruptAffinity or RecommendedAction.EnableMsiMode)
		{
			return ExecutionPhase.DeviceRestart;
		}

		if (PowerPlanScopedIds.Contains(recommendation.Id))
		{
			return ExecutionPhase.PowerPlanScoped;
		}

		return ExecutionPhase.Immediate;
	}
}
