using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>Stops Windows powering down USB hubs and controllers.</summary>
public sealed class UsbHubPowerManagementRule : IRecommendationRule
{
	public string Id => "rec.usb.disable-power-management";

	public string Title => "Disable power management on USB hubs";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		TweakState state = context.StateOf("usb.disable-power-management");
		if (state == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "Every USB hub and controller already has power management turned off.");
		}

		if (context.Profile.UsbControllers.Count == 0)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "No USB host controllers were detected.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.Usb,
			Reasoning =
				"This is the per-device counterpart to selective suspend: the \"Allow the computer to turn " +
				"off this device to save power\" checkbox on every hub and controller. Selective suspend is " +
				"the power-plan policy; this is the device's own permission, and both have to be off for a " +
				"port to stay fully awake.",
			ExpectedBenefit =
				"Removes the remaining path by which a USB port can be powered down under an idle input " +
				"device. Pairs with disabling selective suspend — doing only one leaves the other in effect.",
			Risks = context.Profile.HasBattery
				? "USB controllers stay powered when idle, which costs battery life."
				: "Marginally higher idle power draw.",
			Evidence = new List<Evidence>
			{
				new($"{context.Profile.UsbControllers.Count} USB host controllers and their hubs currently " +
					"allow Windows to power them down.", "USB device tree")
			},
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
			Confidence = RecommendationConfidence.Likely,
			Action = new RecommendedAction.ApplyTweak("usb.disable-power-management"),
			ImpactScore = 45
		});
	}
}

/// <summary>Stops Windows powering down the network adapter.</summary>
public sealed class NetworkPowerManagementRule : IRecommendationRule
{
	public string Id => "rec.network.disable-power-management";

	public string Title => "Disable power management on network adapters";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		if (context.Profile.NetworkAdapters.Count == 0)
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "No physical network adapters were detected on this PC.");
		}

		if (context.StateOf("network.disable-power-management") == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(
				Id, Title, "Network adapter power management is already off.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.Network,
			Reasoning =
				"An adapter allowed to enter a low-power state during a quiet moment has to come back out of " +
				"it before the next packet is handled. For steady traffic this never triggers; for the " +
				"bursty, low-rate traffic pattern of an online game it can.",
			ExpectedBenefit =
				"Removes an occasional wake delay from the receive path. This affects consistency, not " +
				"bandwidth or ping average.",
			Risks = "The adapter stays powered when idle. On a laptop that is a battery cost.",
			Evidence = context.Profile.NetworkAdapters
				.Select(adapter => new Evidence(
					$"{adapter.FriendlyName} currently allows Windows to power it down.",
					"Network adapter configuration"))
				.ToList(),
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
			Confidence = RecommendationConfidence.Likely,
			Action = new RecommendedAction.ApplyTweak("network.disable-power-management"),
			ImpactScore = 40
		});
	}
}

/// <summary>Disables Nagle's algorithm for the network interfaces.</summary>
public sealed class NagleAlgorithmRule : IRecommendationRule
{
	public string Id => "rec.network.nagle";

	public string Title => "Disable Nagle's algorithm";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		if (context.Profile.NetworkAdapters.Count == 0)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "No physical network adapters were detected.");
		}

		TweakState state = context.StateOf("network.disable-nagle");
		if (state == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "Nagle's algorithm is already disabled.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.Network,
			Reasoning =
				"Nagle's algorithm holds a small outgoing TCP segment back until the previous one has been " +
				"acknowledged, so that many tiny writes coalesce into one packet. That is exactly the wrong " +
				"trade for a game sending small, frequent position updates: each one waits for a round trip " +
				"that has nothing to do with it.",
			ExpectedBenefit =
				"Small, frequent TCP sends go out immediately. Worth noting that most modern games use UDP, " +
				"which Nagle does not touch at all — so this helps a narrower set of titles than its " +
				"reputation suggests.",
			Risks =
				"More packets on the wire for the same data, which is slightly less efficient on a congested " +
				"or metered connection. Well-written applications that need this already set TCP_NODELAY " +
				"themselves, making the change redundant for them.",
			Evidence = new List<Evidence>
			{
				new("Nagle's algorithm is active on this PC's network interfaces.", "TCP/IP interface configuration"),
				new($"{context.Profile.NetworkAdapters.Count} physical network adapters would be affected.",
					"Network adapter enumeration")
			},
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
			// Inferred rather than Likely: whether this changes anything depends entirely on whether the
			// application in question uses TCP and has not already disabled Nagle itself.
			Confidence = RecommendationConfidence.Inferred,
			Action = new RecommendedAction.ApplyTweak("network.disable-nagle"),
			ImpactScore = 25
		});
	}
}

/// <summary>Removes the multimedia network throttle.</summary>
public sealed class NetworkThrottlingRule : IRecommendationRule
{
	public string Id => "rec.network.throttling-index";

	public string Title => "Disable network throttling index";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		if (context.Profile.NetworkAdapters.Count == 0)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "No physical network adapters were detected.");
		}

		if (context.StateOf("network.throttling-index") == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "Network throttling is already disabled.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.Network,
			Reasoning =
				"The multimedia class scheduler caps non-multimedia network packet processing at roughly " +
				"10 packets per millisecond while a multimedia stream is playing, so that audio and video " +
				"never starve. On a machine fast enough to do both, the cap protects against a problem that " +
				"no longer exists.",
			ExpectedBenefit =
				"Removes an artificial ceiling on packet processing while media is playing. If you do not " +
				"play audio or video alongside the latency-sensitive application, this changes nothing at " +
				"all — the throttle only engages when a multimedia stream is active.",
			Risks =
				"Under heavy network load with audio playing, audio glitching becomes slightly more likely, " +
				"since the protection that prevented it is what is being removed.",
			Evidence = new List<Evidence>
			{
				new("NetworkThrottlingIndex is at its default rather than disabled.",
					"Multimedia system profile configuration")
			},
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
			Confidence = RecommendationConfidence.Inferred,
			Action = new RecommendedAction.ApplyTweak("network.throttling-index"),
			ImpactScore = 20
		});
	}
}

/// <summary>Turns off Enhance pointer precision.</summary>
public sealed class PointerPrecisionRule : IRecommendationRule
{
	public string Id => "rec.input.pointer-precision";

	public string Title => "Turn off Enhance pointer precision";

	public RuleOutcome Evaluate(RecommendationContext context)
	{
		TweakState state = context.StateOf("mouse.pointer-precision");
		if (state == TweakState.Applied)
		{
			return new RuleOutcome.NotApplicable(Id, Title, "Enhance pointer precision is already off.");
		}

		if (state == TweakState.Unknown)
		{
			return new RuleOutcome.Undetermined(Id, Title, "The pointer precision setting could not be read.");
		}

		return new RuleOutcome.Applicable(new Recommendation
		{
			Id = Id,
			Title = Title,
			Category = RecommendationCategory.System,
			Reasoning =
				"Enhance pointer precision is mouse acceleration: Windows scales the pointer movement by how " +
				"fast the mouse is travelling, so the same physical distance produces a different cursor " +
				"distance depending on speed. That makes the relationship between hand and cursor " +
				"non-constant, which is what prevents muscle memory from forming.",
			ExpectedBenefit =
				"A fixed, repeatable relationship between mouse movement and cursor movement. This is about " +
				"consistency and aim, not about latency — it will not change any number the port test reports.",
			Risks =
				"The pointer will feel slower across large screen distances until you adjust to it, and you " +
				"may want to raise the mouse's own sensitivity to compensate.",
			Evidence = new List<Evidence>
			{
				new("Enhance pointer precision is currently enabled.", "Windows mouse settings")
			},
			Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
			Confidence = RecommendationConfidence.Likely,
			Action = new RecommendedAction.ApplyTweak("mouse.pointer-precision"),
			ImpactScore = 35
		});
	}
}
