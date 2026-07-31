using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Tests;

public class OptimizationPlannerTests
{
	private static RecommendationReport Report(SystemProfile? profile = null) =>
		new RecommendationEngine().Analyze(new RecommendationContext
		{
			Profile = profile ?? TestProfiles.Profile(),
			TweakStates = TestProfiles.AllNotApplied(),
			IsElevated = true
		});

	private static OptimizationPlan PlanFor(ProfileKind kind, SystemProfile? profile = null) =>
		new OptimizationPlanner().Plan(Report(profile), new ProfileCatalog().Get(kind));

	// ---- Catalog integrity -----------------------------------------------------------------

	[Fact]
	public void EveryProfileKindHasExactlyOneDefinition()
	{
		var profiles = new ProfileCatalog().BuildAll();

		Assert.Equal(Enum.GetValues<ProfileKind>().Length, profiles.Count);
		Assert.Equal(profiles.Count, profiles.Select(p => p.Kind).Distinct().Count());
		Assert.Equal(profiles.Count, profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
	}

	[Fact]
	public void EveryProfileIsDescribed()
	{
		Assert.All(new ProfileCatalog().BuildAll(), profile =>
		{
			Assert.False(string.IsNullOrWhiteSpace(profile.Name));
			Assert.False(string.IsNullOrWhiteSpace(profile.Description));
		});
	}

	[Fact]
	public void EveryExclusionAndPriorityReferencesARealRule()
	{
		// A profile that excludes an id which no rule produces is silently doing nothing, and a
		// mistyped priority silently loses its ordering.
		var ruleIds = RecommendationEngine.DefaultRules().Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);

		foreach (OptimizationProfile profile in new ProfileCatalog().BuildAll())
		{
			foreach (string excluded in profile.Exclusions.Keys)
			{
				Assert.Contains(excluded, ruleIds);
			}

			foreach (string priority in profile.Priorities)
			{
				Assert.Contains(priority, ruleIds);
			}
		}
	}

	[Fact]
	public void EveryExclusionCarriesAReason()
	{
		Assert.All(new ProfileCatalog().BuildAll(), profile =>
			Assert.All(profile.Exclusions.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason))));
	}

	// ---- Profiles differ meaningfully ------------------------------------------------------

	[Fact]
	public void ProfilesProduceDifferentPlansFromTheSameMachine()
	{
		var balanced = PlanFor(ProfileKind.Balanced).Steps.Select(s => s.Recommendation.Id).ToList();
		var competitive = PlanFor(ProfileKind.CompetitiveFps).Steps.Select(s => s.Recommendation.Id).ToList();
		var productivity = PlanFor(ProfileKind.Productivity).Steps.Select(s => s.Recommendation.Id).ToList();

		Assert.NotEqual(balanced, competitive);
		Assert.NotEqual(balanced, productivity);
		Assert.NotEqual(competitive, productivity);
	}

	[Fact]
	public void CompetitiveIsASupersetOfProductivityForThisMachine()
	{
		// Productivity is the narrowest profile by design; anything it accepts, the most aggressive
		// profile should accept too.
		var competitive = PlanFor(ProfileKind.CompetitiveFps).Steps.Select(s => s.Recommendation.Id).ToHashSet();
		var productivity = PlanFor(ProfileKind.Productivity).Steps.Select(s => s.Recommendation.Id);

		Assert.All(productivity, id => Assert.Contains(id, competitive));
	}

	[Fact]
	public void BalancedRefusesTheProcessorStateChange()
	{
		var plan = PlanFor(ProfileKind.Balanced);

		Assert.DoesNotContain(plan.Steps, step => step.Recommendation.Id == "rec.cpu.min-processor-state");
		Assert.Contains(plan.Skipped, skipped => skipped.Recommendation.Id == "rec.cpu.min-processor-state");
	}

	[Fact]
	public void BalancedRefusesAnythingNeedingARestart()
	{
		var plan = PlanFor(ProfileKind.Balanced);

		Assert.False(plan.RequiresReboot);
		Assert.All(plan.Steps, step => Assert.NotEqual(ExecutionPhase.RequiresReboot, step.Phase));
	}

	[Fact]
	public void ProductivityRefusesToTradePowerForLatency()
	{
		var plan = PlanFor(ProfileKind.Productivity);

		Assert.All(plan.Steps, step =>
		{
			Assert.NotEqual(RecommendationCategory.Power, step.Recommendation.Category);
			Assert.NotEqual(RecommendationCategory.Cpu, step.Recommendation.Category);
		});
	}

	[Fact]
	public void StreamingKeepsTheMultimediaProtectionsInPlace()
	{
		// The whole point of the Streaming profile: the settings that look like wins elsewhere are the
		// ones that make a stream's audio glitch.
		var plan = PlanFor(ProfileKind.Streaming);

		Assert.DoesNotContain(plan.Steps, step => step.Recommendation.Id == "rec.network.throttling-index");
		Assert.DoesNotContain(plan.Steps, step => step.Recommendation.Id == "rec.network.nagle");

		var skipped = plan.Skipped.Single(s => s.Recommendation.Id == "rec.network.throttling-index");
		Assert.Contains("audio", skipped.Reason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void GamingLeavesTheTcpSettingsAlone()
	{
		var plan = PlanFor(ProfileKind.Gaming);

		Assert.DoesNotContain(plan.Steps, step => step.Recommendation.Id == "rec.network.nagle");
		Assert.Contains(plan.Skipped, s => s.Recommendation.Id == "rec.network.nagle" && s.Reason.Contains("UDP"));
	}

	[Fact]
	public void CompetitiveIncludesTheInterruptLevelWork()
	{
		var plan = PlanFor(ProfileKind.CompetitiveFps);

		Assert.Contains(plan.Steps, step => step.Recommendation.Id == "rec.interrupts.usb-controller-affinity");
	}

	// ---- Refusals that no profile may override ---------------------------------------------

	[Fact]
	public void NoProfileEverAppliesASecurityReducingChange()
	{
		var profile = TestProfiles.Profile(
			windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled, vbs: FeatureState.Enabled));

		foreach (ProfileKind kind in Enum.GetValues<ProfileKind>())
		{
			OptimizationPlan plan = PlanFor(kind, profile);

			Assert.DoesNotContain(plan.Steps, step => step.Recommendation.Id == "rec.security.memory-integrity");
			Assert.All(plan.Steps, step => Assert.Empty(step.Recommendation.Safety.Blockers));
		}
	}

	[Fact]
	public void CustomStillRefusesBlockedChanges()
	{
		// Custom accepts any safety level, but a hard blocker is not a safety level.
		var profile = TestProfiles.Profile(
			windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled));

		var plan = PlanFor(ProfileKind.Custom, profile);

		Assert.Contains(plan.Skipped, s => s.Recommendation.Id == "rec.security.memory-integrity");
	}

	[Fact]
	public void ManualOnlyRecommendationsAreNeverPlanned()
	{
		foreach (ProfileKind kind in Enum.GetValues<ProfileKind>())
		{
			OptimizationPlan plan = PlanFor(kind);

			Assert.All(plan.Steps, step =>
				Assert.IsNotType<RecommendedAction.ManualOnly>(step.Recommendation.Action));
		}
	}

	[Fact]
	public void BlockingConflictsAreExcludedFromEveryProfile()
	{
		var profile = TestProfiles.Profile(windows: TestProfiles.Windows(virtualMachine: true));

		foreach (ProfileKind kind in Enum.GetValues<ProfileKind>())
		{
			OptimizationPlan plan = PlanFor(kind, profile);

			Assert.All(plan.Steps, step => Assert.False(step.Recommendation.HasBlockingConflict));
		}
	}

	// ---- Ordering is correctness -----------------------------------------------------------

	[Fact]
	public void ThePowerPlanSwitchRunsBeforeAnythingScopedToAPlan()
	{
		// Switching plans re-scopes the per-plan settings, so a value written first lands on a scheme
		// that is no longer active and silently has no effect.
		var plan = PlanFor(ProfileKind.Gaming);

		int planSwitch = plan.Steps
			.Select((step, index) => (step, index))
			.Single(pair => pair.step.Recommendation.Id == "rec.power.high-performance-plan").index;

		var scopedIndexes = plan.Steps
			.Select((step, index) => (step, index))
			.Where(pair => pair.step.Phase == ExecutionPhase.PowerPlanScoped)
			.Select(pair => pair.index)
			.ToList();

		Assert.NotEmpty(scopedIndexes);
		Assert.All(scopedIndexes, index => Assert.True(index > planSwitch));
	}

	[Fact]
	public void StepsAreOrderedByPhase()
	{
		var plan = PlanFor(ProfileKind.CompetitiveFps);

		var phases = plan.Steps.Select(step => (int)step.Phase).ToList();

		Assert.Equal(phases.OrderBy(phase => phase), phases);
	}

	[Fact]
	public void RebootRequiringStepsComeLast()
	{
		var plan = PlanFor(ProfileKind.CompetitiveFps);

		if (!plan.RequiresReboot)
		{
			return;
		}

		int firstReboot = plan.Steps
			.Select((step, index) => (step, index))
			.First(pair => pair.step.Phase == ExecutionPhase.RequiresReboot).index;

		Assert.All(
			plan.Steps.Skip(firstReboot),
			step => Assert.Equal(ExecutionPhase.RequiresReboot, step.Phase));
	}

	[Fact]
	public void StepOrderNumbersAreSequentialFromOne()
	{
		var plan = PlanFor(ProfileKind.Gaming);

		Assert.Equal(
			Enumerable.Range(1, plan.Steps.Count),
			plan.Steps.Select(step => step.Order));
	}

	[Fact]
	public void ProfilePrioritiesLeadWithinTheirPhase()
	{
		var plan = PlanFor(ProfileKind.CompetitiveFps);
		var profile = new ProfileCatalog().Get(ProfileKind.CompetitiveFps);

		foreach (var phaseGroup in plan.Steps.GroupBy(step => step.Phase))
		{
			var ordered = phaseGroup.ToList();
			int lastPriorityIndex = ordered.FindLastIndex(
				step => profile.Priorities.Contains(step.Recommendation.Id));

			if (lastPriorityIndex < 0)
			{
				continue;
			}

			Assert.All(
				ordered.Take(lastPriorityIndex + 1),
				step => Assert.Contains(step.Recommendation.Id, profile.Priorities));
		}
	}

	// ---- Plan reporting --------------------------------------------------------------------

	[Fact]
	public void EverySkippedItemCarriesAReason()
	{
		foreach (ProfileKind kind in Enum.GetValues<ProfileKind>())
		{
			OptimizationPlan plan = PlanFor(kind);

			Assert.All(plan.Skipped, skipped => Assert.False(string.IsNullOrWhiteSpace(skipped.Reason)));
		}
	}

	[Fact]
	public void EveryRecommendationIsEitherPlannedOrExplainedAsSkipped()
	{
		RecommendationReport report = Report();
		var plan = new OptimizationPlanner().Plan(report, new ProfileCatalog().Get(ProfileKind.Gaming));

		Assert.Equal(report.Recommendations.Count, plan.Steps.Count + plan.Skipped.Count);
	}

	[Fact]
	public void AnAlreadyTunedMachineProducesAnEmptyPlan()
	{
		// "All tweaks applied" is not the same as "fully tuned": interrupt affinity is not a tweak, so
		// an existing policy has to be supplied separately or the affinity rule still fires.
		SystemProfile profile = TestProfiles.Profile(windows: TestProfiles.Windows(hags: FeatureState.Enabled));

		var report = new RecommendationEngine().Analyze(new RecommendationContext
		{
			Profile = profile,
			TweakStates = TestProfiles.AllApplied(),
			AffinityPolicies = profile.UsbControllers
				.Select(controller => new LatencyBench.Core.Models.HostControllerInfo
				{
					InstanceId = controller.InstanceId,
					FriendlyName = controller.FriendlyName,
					LogicalProcessorCount = profile.Cpu.LogicalProcessorCount,
					Policy = LatencyBench.Core.Models.InterruptAffinityPolicy.SpecifiedProcessors,
					AffinityMask = 0b100
				})
				.ToList(),
			IsElevated = true
		});

		var plan = new OptimizationPlanner().Plan(report, new ProfileCatalog().Get(ProfileKind.Gaming));

		Assert.True(plan.IsEmpty);
		Assert.Equal(100, plan.LowestSafetyScore);
	}

	[Fact]
	public void LowestSafetyScoreReflectsTheWorstStep()
	{
		var plan = PlanFor(ProfileKind.CompetitiveFps);

		Assert.Equal(plan.Steps.Min(step => step.Recommendation.Safety.Score), plan.LowestSafetyScore);
	}
}
