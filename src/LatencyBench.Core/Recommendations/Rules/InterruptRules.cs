using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>
/// Pins the busiest USB host controller's interrupts to a dedicated core.
/// <para>
/// This is the rule with the most machine-specific reasoning in the engine, and the one that most
/// needs measurement rather than assumption: which controller matters depends on where the user's
/// input device is plugged in, and which core to choose depends on the CPU's topology.
/// </para>
/// </summary>
public sealed class UsbControllerAffinityRule : IRecommendationRule
{
    public string Id => "rec.interrupts.usb-controller-affinity";

    public string Title => "Dedicate a core to the USB controller";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        CpuTopology topology = context.Profile.Cpu.Topology;

        // The registry format LatencyBench writes carries a bare 64-bit mask with no group number, so
        // on a machine Windows has split into processor groups it cannot express the target at all.
        if (topology.UsesMultipleProcessorGroups)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                $"This PC has {topology.LogicalProcessorCount} logical processors across " +
                $"{topology.ActiveProcessorGroupCount} processor groups. The interrupt affinity format " +
                "LatencyBench writes cannot address a specific group, so it will not write a partial mask.");
        }

        if (topology.PhysicalCores.Count < 4)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                $"With only {topology.PhysicalCores.Count} physical cores, reserving one for interrupts " +
                "takes away more general scheduling headroom than the pinning gains back.");
        }

        UsbHostControllerSummary? busiest = context.Profile.UsbControllers
            .OrderByDescending(controller => controller.AttachedDeviceCount)
            .FirstOrDefault();

        if (busiest is null)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "No USB host controllers were detected.");
        }

        if (busiest.AttachedDeviceCount == 0)
        {
            return new RuleOutcome.NotApplicable(
                Id, Title, "No devices are attached to any USB controller, so there is nothing to steer.");
        }

        var existing = context.AffinityPolicies.FirstOrDefault(policy =>
            string.Equals(policy.InstanceId, busiest.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && existing.Policy == InterruptAffinityPolicy.SpecifiedProcessors)
        {
            return new RuleOutcome.NotApplicable(
                Id, Title, $"{busiest.FriendlyName} already has an interrupt affinity policy set.");
        }

        PhysicalCore? target = ChooseTargetCore(topology, busiest.NumaNode);
        if (target?.PrimaryLogicalProcessor is not { } logicalProcessor)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "No suitable core could be identified from the processor topology.");
        }

        var evidence = new List<Evidence>
        {
            new($"{busiest.FriendlyName} has {busiest.AttachedDeviceCount} devices attached — the most of any " +
                "controller on this PC.", "USB device tree"),
            new($"Chose core {target.CoreIndex} (logical processor {logicalProcessor}), avoiding core 0.",
                "Processor topology")
        };

        if (topology.IsHybrid)
        {
            evidence.Add(new(
                "This is a hybrid CPU, so the choice was restricted to performance cores.",
                "Processor topology"));
        }

        if (busiest.NumaNode is { } deviceNode && topology.NumaNodes.Count > 1)
        {
            evidence.Add(new(
                $"The controller is attached to NUMA node {deviceNode}, and the chosen core is on " +
                $"node {topology.NumaNodeOf(logicalProcessor)?.ToString() ?? "unknown"}.",
                "Device NUMA node"));
        }

        DpcIsrTestResult? trace = context.LatestTrace;
        if (trace is not null)
        {
            evidence.Add(new(
                $"Most recent trace: {trace.HighSpikesPerSecond:0.00} high spikes/s, worst DPC " +
                $"{trace.HighestDpcMicroseconds:0} µs from {trace.TopDriverName}.",
                "Saved DPC/ISR trace"));
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Interrupts,
            Reasoning =
                $"By default this controller's interrupts can be serviced on any core, including whichever " +
                $"core is busiest at the time. Pinning them to core {target.CoreIndex} means an input report " +
                "is serviced on a core that is not simultaneously doing whatever else the system is doing. " +
                (topology.IsHybrid
                    ? "On a hybrid CPU the choice matters twice over: an efficiency core would service the " +
                      "interrupt more slowly, so only performance cores were considered."
                    : "Core 0 is excluded because Windows routes a disproportionate amount of system work " +
                      "there by default."),
            ExpectedBenefit =
                "Fewer late reports under load. This is the setting most worth measuring before and after — " +
                "run a port test on the device, apply, restart the controller, and run the same test again.",
            Risks =
                "The device must be restarted for the policy to take effect, which briefly disconnects " +
                "everything on that controller. If your keyboard or mouse is on it, they will drop for a " +
                "moment. The policy is removed by clearing the override.",
            Evidence = evidence,
            Safety = SafetyAssessment.Build(
                Reversibility.AutomaticAfterRestart,
                BlastRadius.SingleDevice,
                canDisableDevice: false),
            Confidence = trace is not null ? RecommendationConfidence.Measured : RecommendationConfidence.Inferred,
            Action = new RecommendedAction.SetInterruptAffinity(busiest.InstanceId, new[] { logicalProcessor }),
            ImpactScore = 65
        });
    }

    /// <summary>
    /// Picks the core to hand the interrupts to. Prefers a performance core, never core 0, and takes
    /// the highest-numbered candidate because the low-numbered cores are where Windows concentrates
    /// its own work and where most applications' threads land first.
    /// <para>
    /// When the device reports a NUMA node and the machine has more than one, the choice is confined
    /// to that node. Servicing a device's interrupts on a core in a different node means the handler
    /// reaches the device's memory across the interconnect on every interrupt, which costs more than
    /// the dedicated core saves. The restriction is dropped rather than failing if that node has no
    /// usable core, since a suboptimal pin still beats none.
    /// </para>
    /// </summary>
    private static PhysicalCore? ChooseTargetCore(CpuTopology topology, int? deviceNumaNode)
    {
        IEnumerable<PhysicalCore> candidates = topology.IsHybrid
            ? topology.PerformanceCores
            : topology.PhysicalCores;

        var usable = candidates
            .Where(core => core.PrimaryLogicalProcessor is > 0)
            .OrderByDescending(core => core.CoreIndex)
            .ToList();

        if (deviceNumaNode is { } node && topology.NumaNodes.Count > 1)
        {
            var onNode = usable
                .Where(core => core.PrimaryLogicalProcessor is { } lp && topology.NumaNodeOf(lp) == node)
                .ToList();

            if (onNode.Count > 0)
            {
                return onNode[0];
            }
        }

        return usable.FirstOrDefault();
    }
}

/// <summary>
/// Suggests switching interrupt-capable devices to message-signalled interrupts.
/// </summary>
public sealed class MsiModeRule : IRecommendationRule
{
    public string Id => "rec.interrupts.enable-msi";

    public string Title => "Enable MSI mode on interrupt-heavy devices";

    /// <summary>
    /// Categories where MSI is both well supported and worth having. Deliberately narrow: the
    /// enumerator lists every device with an MSI capability key, and blanket-enabling it across all
    /// of them is how people end up with a device that will not start.
    /// </summary>
    private static readonly string[] WorthwhileCategories =
    {
        "GPU",
        "Network adapter",
        "Storage controller"
    };

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        if (context.InterruptDevices.Count == 0)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The interrupt-capable device list has not been loaded yet.");
        }

        var candidates = context.InterruptDevices
            .Where(device => !device.IsMsiEnabled)
            .Where(device => WorthwhileCategories.Contains(device.CategoryLabel, StringComparer.OrdinalIgnoreCase))
            .OrderBy(device => device.CategoryLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                "Every display, network and storage device that supports MSI already has it enabled.");
        }

        InterruptDeviceInfo primary = candidates[0];

        var evidence = candidates
            .Take(5)
            .Select(device => new Evidence(
                $"{device.CategoryLabel}: {device.FriendlyName} is using line-based interrupts.",
                "Device interrupt configuration"))
            .ToList();

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Interrupts,
            Reasoning =
                "A line-based interrupt is a shared, level-triggered signal: the kernel has to ask each " +
                "device on the line whether it was the one that fired. A message-signalled interrupt is a " +
                "write to a dedicated address, so it is unshared, cannot be confused with another device's, " +
                "and can be routed to a specific core — which is also what makes interrupt affinity work " +
                "properly.",
            ExpectedBenefit =
                "Lower and more consistent ISR time on the affected devices, and interrupt affinity becomes " +
                "meaningful for them.",
            Risks =
                "A device whose driver advertises MSI support but implements it incorrectly can fail to " +
                "start after the change, showing as a yellow warning in Device Manager. It is reverted by " +
                "turning MSI back off for that device and restarting it. Change one device at a time so it " +
                "is obvious which one caused a problem.",
            Evidence = evidence,
            Safety = SafetyAssessment.Build(
                Reversibility.AutomaticAfterRestart,
                BlastRadius.SingleDevice,
                canDisableDevice: true),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.EnableMsiMode(primary.InstanceId),
            ImpactScore = 55
        });
    }
}

/// <summary>
/// Reports the driver a trace blamed for the worst spikes. There is nothing to apply — the fix is
/// a driver change — but naming it is the single most actionable output of a DPC/ISR trace.
/// </summary>
public sealed class DriverLatencyRule : IRecommendationRule
{
    public string Id => "rec.drivers.worst-offender";

    public string Title => "Driver causing the worst latency spikes";

    /// <summary>
    /// A DPC above this is long enough to be felt. Windows' own guidance for a well-behaved DPC
    /// routine is to finish well inside 100 µs; a driver holding the processor beyond that is
    /// delaying every other interrupt behind it.
    /// </summary>
    private const double ConcerningDpcMicroseconds = 500.0;

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        DpcIsrTestResult? trace = context.LatestTrace;
        if (trace is null)
        {
            return new RuleOutcome.Undetermined(
                Id,
                Title,
                "No DPC/ISR trace has been saved yet. Run one from the DPC/ISR tab — this is the only " +
                "recommendation here that can name the specific driver responsible.");
        }

        if (trace.TopDriverMaxMicroseconds < ConcerningDpcMicroseconds)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                $"The worst driver in the last trace ({trace.TopDriverName}) peaked at " +
                $"{trace.TopDriverMaxMicroseconds:0} µs, which is below the {ConcerningDpcMicroseconds:0} µs " +
                "threshold where a DPC starts delaying other work noticeably.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = $"{trace.TopDriverName} is holding the processor too long",
            Category = RecommendationCategory.Drivers,
            Reasoning =
                $"In the last trace, {trace.TopDriverName} produced a deferred procedure call lasting " +
                $"{trace.TopDriverMaxMicroseconds:0} µs. A DPC runs at an IRQL above every thread, so for " +
                "that entire time no thread on that processor runs and no lower-priority interrupt is " +
                "serviced. This is a property of the driver, not of any Windows setting, which is why no " +
                "tweak on this page will fix it.",
            ExpectedBenefit =
                "Replacing or updating the responsible driver typically removes the spikes entirely. No " +
                "amount of affinity or power tuning can compensate for a driver that blocks for this long.",
            Risks = string.Empty,
            Evidence = new List<Evidence>
            {
                new($"{trace.TopDriverName} peaked at {trace.TopDriverMaxMicroseconds:0} µs.", "Saved DPC/ISR trace"),
                new($"Worst DPC overall: {trace.HighestDpcMicroseconds:0} µs; worst ISR: {trace.HighestIsrMicroseconds:0} µs.",
                    "Saved DPC/ISR trace"),
                new($"{trace.HighSpikesPerSecond:0.00} high spikes per second over {trace.DurationSeconds:0} seconds.",
                    "Saved DPC/ISR trace")
            },
            Safety = SafetyAssessment.Build(Reversibility.Manual, BlastRadius.SingleDevice),
            Confidence = RecommendationConfidence.Measured,
            Action = new RecommendedAction.ManualOnly(
                $"Identify which device {trace.TopDriverName} belongs to, then update its driver from the " +
                "hardware vendor rather than through Windows Update. If it belongs to a device you do not " +
                "use, disabling that device in Device Manager removes the spikes outright. Re-run the trace " +
                "afterwards to confirm."),
            ImpactScore = 90
        });
    }
}
