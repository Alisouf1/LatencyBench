using System.Collections.Generic;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>
/// Switches the active plan to High performance.
/// </summary>
public sealed class HighPerformancePlanRule : IRecommendationRule
{
    public string Id => "rec.power.high-performance-plan";

    public string Title => "High performance power plan";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        // On a modern-standby platform Windows hides this plan entirely and drives processor
        // performance from the Power mode slider instead. Suggesting it would produce a button that
        // always fails.
        if (context.Profile.Windows.UsesModernStandby)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                "This PC uses modern standby, where Windows hides the High performance plan and controls " +
                "processor performance through the Power mode slider instead. The individual processor " +
                "settings below still apply to whichever plan is active.");
        }

        TweakState state = context.StateOf("power.high-performance-plan");
        if (state == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "The High performance plan is already active.");
        }

        if (state == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(Id, Title, "The active power plan could not be read.");
        }

        var evidence = new List<Evidence>
        {
            new("The active power plan is not High performance.", "Windows power configuration")
        };

        if (context.Profile.HasBattery)
        {
            evidence.Add(new(
                "This machine has a battery, so the plan also affects runtime away from mains power.",
                "System power status"));
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Power,
            Reasoning =
                "The Balanced plan lets Windows drop processor performance states while the machine looks " +
                "idle. Input handling is bursty and mostly idle by nature, so the CPU is frequently in a " +
                "low state at exactly the moment a report arrives, and the ramp back up happens after the " +
                "work has already been delayed.",
            ExpectedBenefit =
                "More consistent interrupt and DPC servicing, visible as fewer late reports rather than a " +
                "higher average polling rate.",
            Risks = context.Profile.HasBattery
                ? "Noticeably higher power draw and shorter battery life. On a laptop this is a real cost, " +
                  "not a free win."
                : "Higher idle power draw and slightly more heat.",
            Evidence = evidence,
            Safety = SafetyAssessment.Build(
                Reversibility.FullyAutomatic,
                BlastRadius.Subsystem),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("power.high-performance-plan"),
            ImpactScore = context.Profile.HasBattery ? 45 : 60
        });
    }
}

/// <summary>Keeps every logical processor unparked.</summary>
public sealed class CoreParkingRule : IRecommendationRule
{
    public string Id => "rec.cpu.disable-core-parking";

    public string Title => "Disable core parking";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TweakState state = context.StateOf("cpu.disable-core-parking");
        if (state == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "All cores are already kept unparked on the active plan.");
        }

        if (state == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The core parking setting is not exposed on this PC's active power plan.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Cpu,
            Reasoning =
                "A parked core is not merely idle — Windows has removed it from the scheduler's set, and " +
                "bringing it back takes time. An interrupt whose affinity points at a parked core waits " +
                "for that unpark before it can be serviced.",
            ExpectedBenefit =
                "Removes a source of occasional multi-millisecond stalls. Matters most alongside interrupt " +
                "affinity, since pinning a device to a core that Windows is free to park defeats the pinning.",
            Risks = context.Profile.HasBattery
                ? "Higher idle power draw and shorter battery life, since cores that would have been parked " +
                  "stay available."
                : "Slightly higher idle power draw.",
            Evidence = new List<Evidence>
            {
                new("Core parking is enabled on the active power plan.", "Windows power configuration"),
                new($"This CPU has {context.Profile.Cpu.PhysicalCoreCount} physical cores that parking can affect.",
                    "Processor topology")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("cpu.disable-core-parking"),
            ImpactScore = 55
        });
    }
}

/// <summary>Pins the minimum processor state at 100%.</summary>
public sealed class ProcessorMinimumStateRule : IRecommendationRule
{
    public string Id => "rec.cpu.min-processor-state";

    public string Title => "Minimum processor state 100%";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TweakState state = context.StateOf("cpu.min-processor-state");
        if (state == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "The minimum processor state is already 100%.");
        }

        if (state == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The processor state setting is not exposed on this PC's active power plan.");
        }

        // The most aggressive of the power recommendations and the one with the clearest downside.
        // On a laptop it is offered but explicitly de-prioritised rather than quietly presented as a
        // free improvement.
        bool laptop = context.Profile.HasBattery;

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Cpu,
            Reasoning =
                "Holding the floor at 100% stops the processor dropping to a low frequency state during the " +
                "idle gaps between input events. The cost of climbing back out of a low state is paid on the " +
                "very next interrupt, which is what shows up as an occasional late report.",
            ExpectedBenefit =
                "Removes frequency ramp-up from the interrupt path. The effect is on worst-case latency, " +
                "not on average throughput.",
            Risks = laptop
                ? "This is the single largest battery-life cost of any setting here: the CPU never clocks " +
                  "down, on mains or on battery. On a laptop it also means more heat and more fan noise."
                : "Higher idle power draw, more heat, and a constantly higher clock. On a CPU that is " +
                  "thermally limited this can reduce sustained boost under load.",
            Evidence = new List<Evidence>
            {
                new("The minimum processor state on the active plan is below 100%.", "Windows power configuration"),
                new(laptop
                        ? "This machine has a battery, so the cost applies whenever it is unplugged."
                        : "No battery detected, so the cost is power draw and heat rather than runtime.",
                    "System power status")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("cpu.min-processor-state"),
            ImpactScore = laptop ? 30 : 50
        });
    }
}

/// <summary>Stops Windows suspending USB ports to save power.</summary>
public sealed class UsbSelectiveSuspendRule : IRecommendationRule
{
    public string Id => "rec.power.usb-selective-suspend";

    public string Title => "Disable USB selective suspend";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TweakState state = context.StateOf("power.usb-selective-suspend");
        if (state == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "USB selective suspend is already disabled.");
        }

        if (state == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The USB selective suspend setting is not exposed on this PC's active power plan.");
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
                "Selective suspend lets Windows power down a USB port whose device looks idle. A mouse or " +
                "keyboard that has been still for a moment qualifies, and the first report after that has " +
                "to wait for the port to resume.",
            ExpectedBenefit =
                "Removes a resume delay from the first input after a pause. Most noticeable as the input " +
                "device feeling briefly unresponsive after being left alone.",
            Risks = context.Profile.HasBattery
                ? "Higher idle power draw, since USB ports stay powered. Meaningful on battery."
                : "Marginally higher idle power draw.",
            Evidence = new List<Evidence>
            {
                new("USB selective suspend is enabled on the active power plan.", "Windows power configuration"),
                new($"{context.Profile.UsbControllers.Count} USB host controllers are present on this PC.",
                    "USB device tree")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("power.usb-selective-suspend"),
            ImpactScore = 50
        });
    }
}
