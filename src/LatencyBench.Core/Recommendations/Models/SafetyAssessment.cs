using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.Recommendations.Models;

public enum Reversibility
{
	/// <summary>LatencyBench records the prior value and can put it back exactly.</summary>
	FullyAutomatic,

	/// <summary>Revertible, but only after a reboot or a device restart.</summary>
	AutomaticAfterRestart,

	/// <summary>The user must undo it themselves, through Windows or firmware.</summary>
	Manual,

	/// <summary>Cannot be undone.</summary>
	Irreversible
}

public enum BlastRadius
{
	/// <summary>Affects one device.</summary>
	SingleDevice,

	/// <summary>Affects one Windows subsystem — power policy, the network stack.</summary>
	Subsystem,

	/// <summary>Affects how the whole machine boots or schedules.</summary>
	SystemWide
}

public enum SafetyLevel
{
	/// <summary>Nothing to weigh. Reversible, contained, and documented.</summary>
	Safe,

	/// <summary>Reversible, but worth understanding before applying.</summary>
	Cautious,

	/// <summary>A real trade-off the user has to decide on.</summary>
	Consequential,

	/// <summary>Should not be applied automatically under any profile.</summary>
	Dangerous
}

/// <summary>
/// A safety score with its reasoning attached.
/// <para>
/// The score is derived from named factors rather than assigned by hand so that two
/// recommendations with the same score are comparable, and so a user who disagrees with a rating
/// can see exactly which factor produced it. A bare number would be worse than no number at all:
/// it would look objective while being a guess.
/// </para>
/// </summary>
public sealed class SafetyAssessment
{
	/// <summary>0 (never do this automatically) to 100 (nothing to weigh).</summary>
	public required int Score { get; init; }

	public required SafetyLevel Level { get; init; }

	public required Reversibility Reversibility { get; init; }

	public required BlastRadius BlastRadius { get; init; }

	/// <summary>Each deduction applied, and why. Empty when the score is 100.</summary>
	public required IReadOnlyList<string> Deductions { get; init; }

	/// <summary>
	/// Reasons this must never be applied without the user explicitly choosing it — a security
	/// feature being turned off, a setting that can leave a device unusable. Any entry here forces
	/// <see cref="SafetyLevel.Dangerous"/> regardless of the arithmetic.
	/// </summary>
	public required IReadOnlyList<string> Blockers { get; init; }

	public bool IsAutoApplicable => Blockers.Count == 0 && Level <= SafetyLevel.Cautious;

	public static SafetyAssessment Build(
		Reversibility reversibility,
		BlastRadius blastRadius,
		bool requiresReboot = false,
		bool documentedByMicrosoft = true,
		bool reducesSecurity = false,
		bool canDisableDevice = false,
		IEnumerable<string>? extraBlockers = null)
	{
		var deductions = new List<string>();
		var blockers = new List<string>(extraBlockers ?? Array.Empty<string>());
		int score = 100;

		switch (reversibility)
		{
			case Reversibility.AutomaticAfterRestart:
				score -= 10;
				deductions.Add("Undoing it needs a reboot or a device restart.");
				break;
			case Reversibility.Manual:
				score -= 30;
				deductions.Add("LatencyBench cannot undo this for you.");
				break;
			case Reversibility.Irreversible:
				score -= 60;
				deductions.Add("This cannot be undone once applied.");
				blockers.Add("Irreversible changes are never applied automatically.");
				break;
		}

		switch (blastRadius)
		{
			case BlastRadius.Subsystem:
				score -= 5;
				deductions.Add("Affects a whole Windows subsystem, not just one device.");
				break;
			case BlastRadius.SystemWide:
				score -= 15;
				deductions.Add("Changes behaviour for the entire machine.");
				break;
		}

		if (requiresReboot)
		{
			score -= 5;
			deductions.Add("Takes effect only after a restart, so the result cannot be measured immediately.");
		}

		if (!documentedByMicrosoft)
		{
			// The distinction that separates a real optimisation from registry folklore: whether the
			// setting is one Windows documents and supports, or one someone found and shared.
			score -= 25;
			deductions.Add("Not a documented, supported Windows setting — behaviour may change between builds.");
		}

		if (canDisableDevice)
		{
			score -= 25;
			deductions.Add("A wrong value here can leave the device unable to work until it is reverted.");
		}

		if (reducesSecurity)
		{
			score -= 40;
			deductions.Add("Weakens a Windows security protection.");
			blockers.Add("Turning off a security feature is always the user's decision, never an automatic one.");
		}

		score = Math.Clamp(score, 0, 100);

		SafetyLevel level = blockers.Count > 0
			? SafetyLevel.Dangerous
			: score switch
			{
				>= 90 => SafetyLevel.Safe,
				>= 70 => SafetyLevel.Cautious,
				>= 45 => SafetyLevel.Consequential,
				_ => SafetyLevel.Dangerous
			};

		return new SafetyAssessment
		{
			Score = score,
			Level = level,
			Reversibility = reversibility,
			BlastRadius = blastRadius,
			Deductions = deductions,
			Blockers = blockers
		};
	}

	public string Summary => Level switch
	{
		SafetyLevel.Safe => "Safe — reversible and contained.",
		SafetyLevel.Cautious => "Safe to try, worth reading first.",
		SafetyLevel.Consequential => "Real trade-off — decide deliberately.",
		_ => "Do not apply without understanding the cost."
	};
}
