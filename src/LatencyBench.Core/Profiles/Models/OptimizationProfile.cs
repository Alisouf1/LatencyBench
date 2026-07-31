using System;
using System.Collections.Generic;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Profiles.Models;

public enum ProfileKind
{
	Balanced,
	Gaming,
	CompetitiveFps,
	Productivity,
	Streaming,
	Custom
}

/// <summary>
/// A named policy for deciding which recommendations to accept.
/// <para>
/// A profile is not a fixed list of settings. It is a filter over whatever the recommendation
/// engine found on this particular machine, which is what stops the profiles being the same six
/// tweaks under different names on every PC.
/// </para>
/// </summary>
public sealed class OptimizationProfile
{
	public required ProfileKind Kind { get; init; }

	public required string Name { get; init; }

	/// <summary>What this profile is trying to achieve, and what it deliberately will not do.</summary>
	public required string Description { get; init; }

	/// <summary>The most consequential change this profile will accept without being asked.</summary>
	public required SafetyLevel MaximumSafetyLevel { get; init; }

	/// <summary>Whether to accept changes that only take effect after a restart.</summary>
	public required bool AcceptsRebootRequired { get; init; }

	/// <summary>
	/// Whether to accept changes whose main cost is power draw. Set false for profiles where the
	/// machine is expected to run on battery or to stay quiet.
	/// </summary>
	public required bool AcceptsPowerCost { get; init; }

	/// <summary>Categories this profile will consider. Anything outside is left to the user.</summary>
	public required IReadOnlySet<RecommendationCategory> Categories { get; init; }

	/// <summary>
	/// Recommendation ids this profile refuses even though they pass every other filter, each with
	/// the reason. Stated explicitly so a user can see that the omission was a decision rather than
	/// an oversight.
	/// </summary>
	public required IReadOnlyDictionary<string, string> Exclusions { get; init; }

	/// <summary>
	/// Ids this profile treats as its core purpose. Used only for ordering, so the changes the user
	/// chose this profile for are applied before the incidental ones.
	/// </summary>
	public IReadOnlySet<string> Priorities { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
