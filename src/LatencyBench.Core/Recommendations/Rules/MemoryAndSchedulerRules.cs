using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>
/// Restores the client default for foreground/background thread scheduling priority.
/// </summary>
public sealed class SchedulerPrioritySeparationRule : IRecommendationRule
{
	public string Id => "rec.system.scheduler-priority-separation";

	public string Title => "Foreground-app scheduling priority";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		// This registry value ships pointed at "favour background services" on server editions by
		// design — that is correct there, not a problem to fix. Restricting the rule to non-server
		// editions avoids recommending a client-oriented change on a machine where it would be wrong.
		string? editionId = context.Profile.Windows.EditionId;
		if (editionId is not null && editionId.Contains("Server", StringComparison.OrdinalIgnoreCase))
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "This is a Windows Server edition, where favouring background services over " +
				"the foreground app is the correct default rather than something to change.");
		}

		TweakState state = context.StateOf("system.win32-priority-separation");
		if (state == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "Already set to the Windows client default that favours the foreground app.");
		}

		if (state == TweakState.Unknown)
		{
			return new RuleOutcome.Undetermined(Id, Title, "The scheduler priority separation value could not be read.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.Cpu,
			Reasoning =
				"Win32PrioritySeparation controls how the thread scheduler splits CPU quantum length and " +
				"priority boost between the foreground application and background services — the same " +
				"setting behind System Properties > Advanced > Performance Options > \"Adjust for best " +
				"performance of\". This PC's value does not match the default a clean install of this " +
				"Windows edition ships with, which favours the app you are actively using.",
			ExpectedBenefit =
				"The foreground application gets the scheduling priority Windows client is designed to give " +
				"it. Something — a tuning guide written for a server workload, a group policy, another " +
				"utility — most likely changed this away from the default previously.",
			Risks =
				"None beyond the trade the client default itself makes: background services get " +
				"comparatively less CPU time while something is in the foreground, which is what this " +
				"setting is for.",
			Evidence = new List<Evidence>
			{
				new("The current value favours background services rather than the foreground app.",
					"Scheduler priority-separation registry value"),
				new($"Windows edition: {editionId ?? "unknown"}.", "Windows version")
			},
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SystemWide),
			Confidence = RecommendationConfidence.Likely,
			Action = new RecommendedAction.ApplyTweak("system.win32-priority-separation"),
			ImpactScore = 35
		});
	}
}

/// <summary>
/// Detects RAM running below its own rated speed — an XMP/DOCP/EXPO profile that exists in firmware
/// but was never turned on.
/// </summary>
public sealed class MemorySpeedRule : IRecommendationRule
{
	public string Id => "rec.memory.below-rated-speed";

	/// <summary>Minimum gap, in MHz, before the difference is called real rather than firmware
	/// rounding or reporting noise — ground-truthed against a real DDR5 kit where both figures agreed
	/// exactly, so this exists purely as a margin against the cases where they legitimately do not.</summary>
	private const uint MinimumGapMHz = 200;

	public string Title => "Memory running below its rated speed";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		IReadOnlyList<MemoryModuleInfo> modules = context.Profile.MemoryModules;
		if (modules.Count == 0)
		{
			return new RuleOutcome.Undetermined(Id, Title, "Memory module information could not be read.");
		}

		// Zero means the field was not populated by firmware — a real, if unhelpful, answer on some
		// OEM boards — not evidence of anything, so those modules are excluded rather than compared.
		var comparable = modules
			.Where(module => module.RatedSpeedMHz > 0 && module.ConfiguredSpeedMHz > 0)
			.ToList();

		if (comparable.Count == 0)
		{
			return new RuleOutcome.Undetermined(
				Id, Title, "This PC's firmware did not report both a rated and a configured memory speed.");
		}

		var underclocked = comparable
			.Where(module => module.RatedSpeedMHz - module.ConfiguredSpeedMHz >= MinimumGapMHz)
			.ToList();

		if (underclocked.Count == 0)
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "Every memory module is running at (or within normal rounding of) its rated speed.");
		}

		var evidence = underclocked
			.Select(module => new Evidence(
				$"{Describe(module)}: rated {module.RatedSpeedMHz} MHz, running at {module.ConfiguredSpeedMHz} MHz.",
				"WMI memory module information"))
			.ToList();

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.System,
			Reasoning =
				"Windows reports what each memory module is rated for and what it is actually clocked at " +
				"right now. A gap between the two almost always means an XMP, DOCP or EXPO profile exists " +
				"in the module's firmware but has not been enabled in the motherboard's BIOS/UEFI setup, " +
				"so the module is running at its conservative JEDEC fallback speed instead.",
			ExpectedBenefit =
				"Lower memory latency and higher bandwidth across everything the CPU touches, since memory " +
				"speed is on the critical path for essentially all work, not specifically interrupt " +
				"handling. This is the largest-impact change in this list for general responsiveness, but " +
				"it is a BIOS setting — Windows cannot enable it.",
			Risks =
				"XMP/DOCP/EXPO profiles are validated by the memory maker for their kit but not by the " +
				"motherboard maker for every board and CPU combination. Instability after enabling it " +
				"(failure to boot, memory errors) is uncommon on modern platforms but not impossible; the " +
				"fix is disabling the profile again in BIOS/UEFI setup.",
			Evidence = evidence,
			Safety = SafetyAssessment.Build(Reversibility.Manual, BlastRadius.SystemWide, requiresReboot: true),
			Confidence = RecommendationConfidence.Measured,
			Action = new RecommendedAction.ManualOnly(
				"Restart into BIOS/UEFI setup (usually Del or F2 during boot) and look for XMP, DOCP, EXPO, " +
				"or \"Memory Profile\" — usually on the main or Overclocking page — and enable it. Save and " +
				"exit, then confirm in Windows that the configured speed now matches the rated speed."),
			RequiresReboot = true,
			ImpactScore = 70
		});
	}

	private static string Describe(MemoryModuleInfo module) =>
		module.BankLabel ?? module.PartNumber ?? module.Manufacturer ?? "Memory module";
}
