using System.Collections.Generic;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>
/// Memory integrity is the single largest platform-level contributor to interrupt latency that a
/// user can actually turn off. It is also a real security protection, so this rule never offers to
/// do it — it explains the trade and points at the Windows setting.
/// </summary>
public sealed class MemoryIntegrityRule : IRecommendationRule
{
    public string Id => "rec.security.memory-integrity";

    public string Title => "Memory integrity (Core isolation)";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        WindowsInfo windows = context.Profile.Windows;

        if (windows.MemoryIntegrity == FeatureState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "Windows did not report whether memory integrity is running.");
        }

        if (windows.MemoryIntegrity == FeatureState.Disabled)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                windows.VirtualizationBasedSecurity == FeatureState.Enabled
                    ? "Memory integrity is already off. Virtualisation-based security is still running for " +
                      "other reasons — the Virtual Machine Platform, WSL, or a hypervisor — which carries " +
                      "some of the same cost, but memory integrity itself is not adding to it."
                    : "Memory integrity is already off.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Security,
            Reasoning =
                "Memory integrity runs the kernel under a hypervisor and validates driver code in a separate " +
                "virtual trust level. Every interrupt and DPC therefore crosses a virtualisation boundary " +
                "that would not exist otherwise, which raises both the average and, more importantly, the " +
                "worst-case servicing time.",
            ExpectedBenefit =
                "This is typically the largest single reduction in DPC latency available on a modern " +
                "Windows 11 machine. Measure before and after rather than taking that on faith.",
            Risks =
                "This is a genuine security protection, not a placebo. Turning it off removes the check that " +
                "stops an unsigned or tampered driver being loaded into the kernel. If you do not have a " +
                "specific measured latency problem, the correct choice is to leave it on.",
            Evidence = new List<Evidence>
            {
                new("Memory integrity is currently running.", "Windows Device Guard status"),
                new(
                    windows.VirtualizationBasedSecurity == FeatureState.Enabled
                        ? "Virtualisation-based security is active on this machine."
                        : "Virtualisation-based security was not reported as active.",
                    "Windows Device Guard status")
            },
            Safety = SafetyAssessment.Build(
                Reversibility.Manual,
                BlastRadius.SystemWide,
                requiresReboot: true,
                reducesSecurity: true),
            Confidence = RecommendationConfidence.Likely,
            // Deliberately not an ApplyTweak. LatencyBench will not disable a security feature on the
            // user's behalf, under any profile, however the safety score comes out.
            Action = new RecommendedAction.ManualOnly(
                "Windows Security → Device security → Core isolation details → turn off Memory integrity, then reboot. " +
                "Re-run a DPC/ISR trace afterwards and compare it against the one you took first."),
            RequiresReboot = true,
            ImpactScore = 85
        });
    }
}

/// <summary>Hardware-accelerated GPU scheduling.</summary>
public sealed class HardwareGpuSchedulingRule : IRecommendationRule
{
    public string Id => "rec.gpu.hardware-scheduling";

    public string Title => "Hardware-accelerated GPU scheduling";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        WindowsInfo windows = context.Profile.Windows;

        if (!windows.SupportsHardwareAcceleratedGpuScheduling)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                "No display driver on this PC reports support for hardware-accelerated GPU scheduling. " +
                "Setting it would write a registry value that Windows then ignores.");
        }

        if (windows.HardwareAcceleratedGpuScheduling == FeatureState.Enabled)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "Hardware-accelerated GPU scheduling is already on.");
        }

        if (context.StateOf("gpu.hardware-scheduling") == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(
                Id, Title, "Already enabled — it will take effect after the next restart.");
        }

        var evidence = new List<Evidence>
        {
            new("Hardware-accelerated GPU scheduling is supported but not enabled.", "Graphics driver configuration")
        };

        foreach (GpuInfo gpu in context.Profile.Gpus)
        {
            if (gpu.IsPrimary)
            {
                evidence.Add(new($"Primary display adapter: {gpu.Name} (driver {gpu.DriverVersion}).", "Display adapter"));
            }
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Gpu,
            Reasoning =
                "With this off, the CPU builds and submits GPU work through the Windows scheduler on every " +
                "frame. With it on, the GPU's own scheduling processor manages its queues, which removes " +
                "that per-frame CPU work and the DPC activity from the display driver that goes with it.",
            ExpectedBenefit =
                "Lower display-driver DPC activity, which shows up in a trace as fewer spikes attributed to " +
                "the graphics driver. Frame-rate effects are small and can go either way.",
            Risks =
                "Requires a restart. A small number of capture and overlay tools behave differently with it " +
                "on. It is reverted the same way it was applied.",
            Evidence = evidence,
            Safety = SafetyAssessment.Build(
                Reversibility.AutomaticAfterRestart,
                BlastRadius.Subsystem,
                requiresReboot: true),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak("gpu.hardware-scheduling"),
            RequiresReboot = true,
            ImpactScore = 45
        });
    }
}

/// <summary>
/// Everything else in this engine reasons about physical hardware. In a guest that reasoning is
/// wrong at the root, so this rule says so once rather than letting a dozen rules each give
/// confident advice about devices the hypervisor invented.
/// </summary>
public sealed class VirtualMachineRule : IRecommendationRule
{
    public string Id => "rec.system.virtual-machine";

    public string Title => "Running inside a virtual machine";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        if (!context.Profile.Windows.IsVirtualMachine)
        {
            return new RuleOutcome.NotApplicable(Id, Title, "This is a physical machine.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.System,
            Reasoning =
                "This Windows install is a guest. The USB controllers, storage controllers and network " +
                "adapters it can see are emulated or paravirtualised devices, and their interrupt behaviour " +
                "is decided by the host, not by settings inside the guest.",
            ExpectedBenefit =
                "None from the settings on this page. Latency inside a guest is dominated by the host's " +
                "scheduling and by device passthrough configuration, both of which are changed on the host.",
            Risks =
                "Applying interrupt affinity or MSI changes to virtual devices is not dangerous, but the " +
                "measurements afterwards will describe the hypervisor rather than any real hardware.",
            Evidence = new List<Evidence>
            {
                new("Firmware identity strings match a known hypervisor.", "SMBIOS system information")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ManualOnly(
                "Tune latency on the host instead: CPU pinning for the guest, and device passthrough for any " +
                "peripheral whose latency actually matters."),
            ImpactScore = 100
        });
    }
}
