using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Profiles;

/// <summary>
/// The built-in profiles.
/// <para>
/// Each one differs in what it is willing to trade, not merely in how many switches it flips. The
/// exclusions carry their reasons because a profile that quietly skips something looks like a bug.
/// </para>
/// </summary>
public sealed class ProfileCatalog
{
	private static readonly RecommendationCategory[] AllCategories =
		Enum.GetValues<RecommendationCategory>();

	public IReadOnlyList<OptimizationProfile> BuildAll() => new[]
	{
		Balanced(),
		Gaming(),
		CompetitiveFps(),
		Productivity(),
		Streaming(),
		Custom()
	};

	public OptimizationProfile Get(ProfileKind kind) =>
		BuildAll().First(profile => profile.Kind == kind);

	/// <summary>
	/// The default. Only changes that are reversible, contained, take effect immediately, and cost
	/// nothing but a little idle power.
	/// </summary>
	private static OptimizationProfile Balanced() => new()
	{
		Kind = ProfileKind.Balanced,
		Name = "Balanced",
		Description =
			"Applies only changes that are fully reversible, take effect immediately, and have no " +
			"meaningful downside. Nothing that needs a restart, nothing that trades a real amount of " +
			"power or heat, and nothing that touches security. This is the profile to use if you are " +
			"not sure.",
		MaximumSafetyLevel = SafetyLevel.Safe,
		AcceptsRebootRequired = false,
		AcceptsPowerCost = false,
		Categories = Set(AllCategories),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["rec.cpu.min-processor-state"] =
				"Holding the processor at 100% is the largest power and heat cost of any setting here. " +
				"Balanced leaves it to you.",
			["rec.network.throttling-index"] =
				"The throttle only engages while media is playing, and removing it makes audio glitching " +
				"slightly more likely under load. Too little upside for this profile."
		}
	};

	/// <summary>General gaming: accepts power cost and a restart, leaves the network stack alone.</summary>
	private static OptimizationProfile Gaming() => new()
	{
		Kind = ProfileKind.Gaming,
		Name = "Gaming",
		Description =
			"Prioritises consistent input and frame delivery. Accepts higher idle power draw and changes " +
			"that need a restart. Leaves the TCP settings alone, because most games use UDP and would not " +
			"notice them.",
		MaximumSafetyLevel = SafetyLevel.Cautious,
		AcceptsRebootRequired = true,
		AcceptsPowerCost = true,
		Categories = Set(AllCategories),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["rec.network.nagle"] =
				"Nagle only affects TCP. Most games use UDP, where this changes nothing, and titles that " +
				"do use TCP and care already set TCP_NODELAY themselves.",
			["rec.network.throttling-index"] =
				"Only engages while media is playing, and the protection it removes is the one that keeps " +
				"audio from glitching under network load."
		},
		Priorities = Set(
			"rec.power.high-performance-plan",
			"rec.cpu.disable-core-parking",
			"rec.power.usb-selective-suspend",
			"rec.gpu.hardware-scheduling")
	};

	/// <summary>
	/// The most aggressive automatic profile. Everything Gaming does, plus the interrupt-level work
	/// and the input settings that matter for aim.
	/// </summary>
	private static OptimizationProfile CompetitiveFps() => new()
	{
		Kind = ProfileKind.CompetitiveFps,
		Name = "Competitive FPS",
		Description =
			"Everything the Gaming profile does, plus interrupt-level tuning: dedicating a core to the " +
			"busiest USB controller, message-signalled interrupts where they help, and turning off mouse " +
			"acceleration. Expect to restart devices, and measure before and after — this is the profile " +
			"where that actually matters.",
		MaximumSafetyLevel = SafetyLevel.Cautious,
		AcceptsRebootRequired = true,
		AcceptsPowerCost = true,
		Categories = Set(AllCategories),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["rec.network.throttling-index"] =
				"Only engages while media is playing. If you play with music or a stream running, removing " +
				"the throttle makes that audio more likely to glitch."
		},
		Priorities = Set(
			"rec.interrupts.usb-controller-affinity",
			"rec.interrupts.enable-msi",
			"rec.input.pointer-precision",
			"rec.power.high-performance-plan",
			"rec.power.usb-selective-suspend")
	};

	/// <summary>
	/// For a machine that has to stay quiet, cool, and long-running on battery. Deliberately the
	/// narrowest profile: it takes only the changes that cost nothing at all.
	/// </summary>
	private static OptimizationProfile Productivity() => new()
	{
		Kind = ProfileKind.Productivity,
		Name = "Productivity",
		Description =
			"Removes stalls without trading power, heat, or fan noise for them. Skips everything whose " +
			"benefit is worst-case latency you would not notice in a document or a browser, and keeps " +
			"the machine quiet and cool.",
		MaximumSafetyLevel = SafetyLevel.Safe,
		AcceptsRebootRequired = false,
		AcceptsPowerCost = false,
		Categories = Set(
			RecommendationCategory.Usb,
			RecommendationCategory.System,
			RecommendationCategory.Storage,
			RecommendationCategory.Drivers),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["rec.cpu.min-processor-state"] =
				"Never clocking down is the opposite of what this profile is for.",
			["rec.cpu.disable-core-parking"] =
				"Parked cores are how the machine stays cool and quiet when it is not busy.",
			["rec.power.high-performance-plan"] =
				"The plan raises idle power draw across the board for latency this workload will not notice."
		}
	};

	/// <summary>
	/// Streaming is the case where several of the "obvious" latency tweaks are actively wrong: the
	/// machine is encoding and pushing audio and video continuously, which is exactly the workload
	/// the multimedia protections exist for.
	/// </summary>
	private static OptimizationProfile Streaming() => new()
	{
		Kind = ProfileKind.Streaming,
		Name = "Streaming",
		Description =
			"Tuned for a machine that is capturing, encoding and uploading while you use it. Keeps the " +
			"processor responsive and the input path clean, but deliberately leaves the multimedia and " +
			"network protections in place — they exist for exactly this workload, and removing them " +
			"shows up as glitched audio in the stream rather than as lower latency.",
		MaximumSafetyLevel = SafetyLevel.Cautious,
		AcceptsRebootRequired = true,
		AcceptsPowerCost = true,
		Categories = Set(AllCategories),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["rec.network.throttling-index"] =
				"This is the single worst change for a streaming machine. The throttle exists to stop " +
				"network traffic starving audio and video playback, and a stream is that traffic.",
			["rec.network.nagle"] =
				"More small packets on the wire competes with the upload the encoder needs, for a benefit " +
				"that only applies to TCP games.",
			["rec.network.disable-power-management"] =
				"The adapter is under continuous load while streaming, so it never enters a low-power " +
				"state for this to prevent."
		},
		Priorities = Set(
			"rec.cpu.disable-core-parking",
			"rec.power.usb-selective-suspend",
			"rec.gpu.hardware-scheduling")
	};

	/// <summary>Applies nothing on its own — the user chooses each item.</summary>
	private static OptimizationProfile Custom() => new()
	{
		Kind = ProfileKind.Custom,
		Name = "Custom",
		Description =
			"No automatic filtering. Every recommendation is shown with its reasoning, safety score and " +
			"conflicts, and nothing is applied until you pick it.",
		MaximumSafetyLevel = SafetyLevel.Dangerous,
		AcceptsRebootRequired = true,
		AcceptsPowerCost = true,
		Categories = Set(AllCategories),
		Exclusions = new Dictionary<string, string>(StringComparer.Ordinal)
	};

	private static IReadOnlySet<RecommendationCategory> Set(params RecommendationCategory[] categories) =>
		new HashSet<RecommendationCategory>(categories);

	private static IReadOnlySet<string> Set(params string[] values) =>
		new HashSet<string>(values, StringComparer.Ordinal);
}
