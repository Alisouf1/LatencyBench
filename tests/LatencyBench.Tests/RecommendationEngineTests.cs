using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Recommendations.Rules;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

public class RecommendationEngineTests
{
    private static RecommendationContext Context(
        SystemProfile? profile = null,
        Dictionary<string, TweakState>? tweakStates = null,
        IReadOnlyList<InterruptDeviceInfo>? interruptDevices = null,
        IReadOnlyList<HostControllerInfo>? affinityPolicies = null,
        IReadOnlyList<DpcIsrTestResult>? traces = null) => new()
        {
            Profile = profile ?? TestProfiles.Profile(),
            TweakStates = tweakStates ?? TestProfiles.AllNotApplied(),
            InterruptDevices = interruptDevices ?? Array.Empty<InterruptDeviceInfo>(),
            AffinityPolicies = affinityPolicies ?? Array.Empty<HostControllerInfo>(),
            DpcIsrTraces = traces ?? Array.Empty<DpcIsrTestResult>(),
            IsElevated = true
        };

    private static DpcIsrTestResult Trace(
        string topDriver = "nvlddmkm.sys",
        double topDriverMax = 1200,
        double highestDpc = 1500) => new()
        {
            DurationSeconds = 30,
            HighSpikesPerSecond = 1.2,
            BorderlineSpikesPerSecond = 4.0,
            HighestDpcMicroseconds = highestDpc,
            HighestIsrMicroseconds = 90,
            TopDriverName = topDriver,
            TopDriverMaxMicroseconds = topDriverMax,
            SavedAt = DateTime.Now
        };

    // ---- Structural guarantees -------------------------------------------------------------

    [Fact]
    public void EveryApplyTweakActionReferencesARealTweak()
    {
        // This is the guard for a whole class of silent breakage: a rule that names a tweak id which
        // does not exist produces a recommendation with an Apply button that can never work. It was a
        // real bug — two rules referenced "input.pointer-precision" and "network.nagle" when the
        // catalog defines "mouse.pointer-precision" and "network.disable-nagle".
        var catalogIds = new TweakCatalog().BuildAll()
            .Select(tweak => tweak.Definition.Id)
            .ToHashSet(StringComparer.Ordinal);

        var report = new RecommendationEngine().Analyze(Context());

        var referenced = report.Recommendations
            .Select(recommendation => recommendation.Action)
            .OfType<RecommendedAction.ApplyTweak>()
            .Select(action => action.TweakId)
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, id => Assert.Contains(id, catalogIds));
    }

    [Fact]
    public void EveryRuleReferencesOnlyTweakIdsThatExist()
    {
        // Broader than the test above: drives every rule through a machine state designed to make it
        // fire, so rules that only fire in unusual conditions are covered too.
        var catalogIds = new TweakCatalog().BuildAll()
            .Select(tweak => tweak.Definition.Id)
            .ToHashSet(StringComparer.Ordinal);

        var contexts = new[]
        {
            Context(),
            Context(TestProfiles.Profile(hasBattery: true)),
            Context(TestProfiles.Profile(windows: TestProfiles.Windows(hags: FeatureState.Disabled))),
            Context(TestProfiles.Profile(windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled))),
            Context(tweakStates: TestProfiles.AllApplied())
        };

        foreach (var context in contexts)
        {
            foreach (var recommendation in new RecommendationEngine().Analyze(context).Recommendations)
            {
                if (recommendation.Action is RecommendedAction.ApplyTweak apply)
                {
                    Assert.Contains(apply.TweakId, catalogIds);
                }
            }
        }
    }

    [Fact]
    public void RuleIdsAreUnique()
    {
        var ids = RecommendationEngine.DefaultRules().Select(rule => rule.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryRuleHasAnIdAndTitle()
    {
        Assert.All(RecommendationEngine.DefaultRules(), rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.False(string.IsNullOrWhiteSpace(rule.Title));
        });
    }

    [Fact]
    public void EveryRecommendationIsFullyExplained()
    {
        // A recommendation without reasoning, an expected benefit, or evidence is indistinguishable
        // from the folklore this engine exists to replace.
        var report = new RecommendationEngine().Analyze(Context(
            profile: TestProfiles.Profile(windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled)),
            traces: new[] { Trace() }));

        Assert.NotEmpty(report.Recommendations);
        Assert.All(report.Recommendations, recommendation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recommendation.Reasoning));
            Assert.False(string.IsNullOrWhiteSpace(recommendation.ExpectedBenefit));
            Assert.NotEmpty(recommendation.Evidence);
            Assert.All(recommendation.Evidence, evidence =>
            {
                Assert.False(string.IsNullOrWhiteSpace(evidence.Observation));
                Assert.False(string.IsNullOrWhiteSpace(evidence.Source));
            });
            Assert.InRange(recommendation.Safety.Score, 0, 100);
            Assert.InRange(recommendation.ImpactScore, 0, 100);
        });
    }

    [Fact]
    public void EveryRuleAnswersEvenWhenItDoesNotFire()
    {
        // Silence is what makes a recommendation engine feel arbitrary. With everything already
        // applied, every rule should still account for itself one way or another.
        var report = new RecommendationEngine().Analyze(Context(tweakStates: TestProfiles.AllApplied()));

        int accounted = report.Recommendations.Count + report.NotApplicable.Count + report.Undetermined.Count;

        Assert.Equal(RecommendationEngine.DefaultRules().Count, accounted);
        Assert.Empty(report.Failures);
        Assert.All(report.NotApplicable, outcome => Assert.False(string.IsNullOrWhiteSpace(outcome.Reason)));
        Assert.All(report.Undetermined, outcome => Assert.False(string.IsNullOrWhiteSpace(outcome.MissingInformation)));
    }

    [Fact]
    public void NoRuleThrowsOnAMinimalMachine()
    {
        // A single-core machine with no devices at all: nothing detected, nothing measured.
        var bare = TestProfiles.Profile(
            topology: TestProfiles.Topology(physicalCores: 1, threadsPerCore: 1),
            usbControllers: 0,
            networkAdapters: 0,
            gpus: Array.Empty<GpuInfo>());

        var report = new RecommendationEngine().Analyze(Context(bare));

        Assert.Empty(report.Failures);
    }

    [Fact]
    public void ABrokenRuleDoesNotTakeDownTheAnalysis()
    {
        var rules = new List<IRecommendationRule> { new ThrowingRule() };
        rules.AddRange(RecommendationEngine.DefaultRules());

        var report = new RecommendationEngine(rules).Analyze(Context());

        Assert.Single(report.Failures);
        Assert.Contains("Deliberately broken", report.Failures[0]);
        Assert.NotEmpty(report.Recommendations);
    }

    private sealed class ThrowingRule : IRecommendationRule
    {
        public string Id => "rec.test.throwing";

        public string Title => "Deliberately broken rule";

        public RuleOutcome Evaluate(RecommendationContext context) => throw new InvalidOperationException("boom");
    }

    // ---- Rule behaviour --------------------------------------------------------------------

    [Fact]
    public void ModernStandbySuppressesTheHighPerformancePlanRecommendation()
    {
        // On these machines Windows hides the plan, so the button would always fail.
        var context = Context(TestProfiles.Profile(windows: TestProfiles.Windows(modernStandby: true)));

        var report = new RecommendationEngine().Analyze(context);

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.power.high-performance-plan");
        Assert.Contains(report.NotApplicable, outcome =>
            outcome.RuleId == "rec.power.high-performance-plan" && outcome.Reason.Contains("modern standby"));
    }

    [Fact]
    public void UnsupportedHardwareSchedulingIsExplainedRatherThanOffered()
    {
        var context = Context(TestProfiles.Profile(
            windows: TestProfiles.Windows(hags: FeatureState.Disabled, hagsSupported: false)));

        var report = new RecommendationEngine().Analyze(context);

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.gpu.hardware-scheduling");
        Assert.Contains(report.NotApplicable, outcome => outcome.RuleId == "rec.gpu.hardware-scheduling");
    }

    [Fact]
    public void MemoryIntegrityIsNeverOfferedAsAnAutomaticAction()
    {
        // Turning off a security feature has to stay the user's explicit decision.
        var context = Context(TestProfiles.Profile(
            windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled, vbs: FeatureState.Enabled)));

        var report = new RecommendationEngine().Analyze(context);

        var recommendation = Assert.Single(
            report.Recommendations.Where(r => r.Id == "rec.security.memory-integrity"));

        Assert.IsType<RecommendedAction.ManualOnly>(recommendation.Action);
        Assert.Equal(SafetyLevel.Dangerous, recommendation.Safety.Level);
        Assert.NotEmpty(recommendation.Safety.Blockers);
        Assert.False(recommendation.Safety.IsAutoApplicable);
        Assert.DoesNotContain(report.AutoApplicable, r => r.Id == "rec.security.memory-integrity");
    }

    [Fact]
    public void AffinityIsNotRecommendedOnAMultiGroupMachine()
    {
        // The registry format LatencyBench writes has no group field, so it must refuse rather than
        // write a mask that addresses the wrong processors.
        var context = Context(TestProfiles.Profile(
            topology: TestProfiles.Topology(physicalCores: 64, threadsPerCore: 2, processorGroups: 2)));

        var report = new RecommendationEngine().Analyze(context);

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.interrupts.usb-controller-affinity");
        Assert.Contains(report.NotApplicable, outcome =>
            outcome.RuleId == "rec.interrupts.usb-controller-affinity"
            && outcome.Reason.Contains("processor groups"));
    }

    [Fact]
    public void AffinityIsNotRecommendedOnALowCoreCountMachine()
    {
        var context = Context(TestProfiles.Profile(
            topology: TestProfiles.Topology(physicalCores: 2, threadsPerCore: 2)));

        var report = new RecommendationEngine().Analyze(context);

        Assert.Contains(report.NotApplicable, outcome =>
            outcome.RuleId == "rec.interrupts.usb-controller-affinity");
    }

    [Fact]
    public void AffinityNeverTargetsLogicalProcessorZero()
    {
        var report = new RecommendationEngine().Analyze(Context());

        var affinity = report.Recommendations
            .Select(r => r.Action)
            .OfType<RecommendedAction.SetInterruptAffinity>()
            .ToList();

        Assert.NotEmpty(affinity);
        Assert.All(affinity, action => Assert.DoesNotContain(0, action.Cores));
    }

    [Fact]
    public void AffinityAvoidsEfficiencyCoresOnAHybridCpu()
    {
        // 12 cores, the last 4 of which are efficiency cores. Their logical processors start at 16.
        var topology = TestProfiles.Topology(physicalCores: 12, threadsPerCore: 2, efficiencyCores: 4);
        var efficiencyLogicalProcessors = topology.PhysicalCores
            .Where(core => core.Class == CoreClass.Efficiency)
            .SelectMany(core => core.LogicalProcessors)
            .ToHashSet();

        Assert.NotEmpty(efficiencyLogicalProcessors);

        var report = new RecommendationEngine().Analyze(Context(TestProfiles.Profile(topology: topology)));

        var action = report.Recommendations
            .Select(r => r.Action)
            .OfType<RecommendedAction.SetInterruptAffinity>()
            .Single();

        Assert.All(action.Cores, core => Assert.DoesNotContain(core, efficiencyLogicalProcessors));
    }

    [Fact]
    public void AffinityTargetsTheBusiestUsbController()
    {
        var profile = TestProfiles.Profile(usbControllers: 3, usbDevicesOnBusiest: 9);

        var report = new RecommendationEngine().Analyze(Context(profile));

        var action = report.Recommendations
            .Select(r => r.Action)
            .OfType<RecommendedAction.SetInterruptAffinity>()
            .Single();

        Assert.Equal(profile.UsbControllers[0].InstanceId, action.InstanceId);
    }

    [Fact]
    public void AffinityKeepsTheChosenCoreOnTheDevicesNumaNode()
    {
        // Untestable on the development machine, which has one node. Servicing a device's interrupts
        // on a core in another node means crossing the interconnect on every interrupt, which costs
        // more than the dedicated core saves.
        var topology = TestProfiles.Topology(physicalCores: 16, threadsPerCore: 2, numaNodes: 2);
        var profile = TestProfiles.Profile(topology: topology);

        // Node 1 in the test topology holds the odd-numbered logical processors.
        var onNodeOne = profile.UsbControllers
            .Select((controller, index) => index == 0 ? controller with { NumaNode = 1 } : controller)
            .ToList();

        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(topology: topology, usbControllerOverride: onNodeOne)));

        var action = report.Recommendations
            .Select(r => r.Action)
            .OfType<RecommendedAction.SetInterruptAffinity>()
            .Single();

        int chosen = action.Cores.Single();
        Assert.Equal(1, topology.NumaNodeOf(chosen));
    }

    [Fact]
    public void AffinityIgnoresNumaOnASingleNodeMachine()
    {
        // A single-node machine reports no node at all for its devices, and the restriction must not
        // then eliminate every candidate.
        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(topology: TestProfiles.Topology(physicalCores: 8, numaNodes: 1))));

        Assert.Contains(report.Recommendations, r => r.Id == "rec.interrupts.usb-controller-affinity");
    }

    // ---- Memory speed -----------------------------------------------------------------------

    [Fact]
    public void MemorySpeedIsQuietWhenEveryModuleIsAtRatedSpeed()
    {
        // TestProfiles' default fixture matches the real ground-truthed reference machine: rated and
        // configured both 6000 MHz.
        var report = new RecommendationEngine().Analyze(Context());

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.memory.below-rated-speed");
        Assert.Contains(report.NotApplicable, outcome => outcome.RuleId == "rec.memory.below-rated-speed");
    }

    [Fact]
    public void MemorySpeedFiresWhenRunningWellBelowRated()
    {
        var profile = TestProfiles.Profile(memoryModules: new[]
        {
            new MemoryModuleInfo("P0 CHANNEL A", "Vendor", "PART-1", 16UL * 1024 * 1024 * 1024, 6000, 4800)
        });

        var report = new RecommendationEngine().Analyze(Context(profile));

        var recommendation = Assert.Single(report.Recommendations.Where(r => r.Id == "rec.memory.below-rated-speed"));
        Assert.Equal(RecommendationConfidence.Measured, recommendation.Confidence);
        Assert.IsType<RecommendedAction.ManualOnly>(recommendation.Action);
        // BankLabel is preferred over PartNumber when both are present — matching what Device Manager
        // itself calls the slot ("P0 CHANNEL A"), which is what a user can actually see and act on.
        Assert.Contains("P0 CHANNEL A", recommendation.Evidence[0].Observation);
    }

    [Fact]
    public void MemorySpeedIgnoresATinyGapWithinRoundingMargin()
    {
        // 6000 vs 5900 is inside the 200 MHz noise floor and must not be reported as a real finding.
        var profile = TestProfiles.Profile(memoryModules: new[]
        {
            new MemoryModuleInfo(null, null, null, 16UL * 1024 * 1024 * 1024, 6000, 5900)
        });

        var report = new RecommendationEngine().Analyze(Context(profile));

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.memory.below-rated-speed");
    }

    [Fact]
    public void MemorySpeedTreatsAZeroFieldAsUnpopulatedRatherThanAFinding()
    {
        // Some OEM firmware leaves one of the two fields at 0. That is "not reported", not "running
        // at 0 MHz", and must not be compared as if it were a real reading.
        var profile = TestProfiles.Profile(memoryModules: new[]
        {
            new MemoryModuleInfo(null, null, null, 16UL * 1024 * 1024 * 1024, 0, 4800)
        });

        var report = new RecommendationEngine().Analyze(Context(profile));

        Assert.Contains(report.Undetermined, outcome => outcome.RuleId == "rec.memory.below-rated-speed");
    }

    [Fact]
    public void MemorySpeedIsUndeterminedWithNoModulesReported()
    {
        var profile = TestProfiles.Profile(memoryModules: Array.Empty<MemoryModuleInfo>());

        var report = new RecommendationEngine().Analyze(Context(profile));

        Assert.Contains(report.Undetermined, outcome => outcome.RuleId == "rec.memory.below-rated-speed");
    }

    [Fact]
    public void MemorySpeedReportsEveryUnderclockedModuleNotJustTheFirst()
    {
        var profile = TestProfiles.Profile(memoryModules: new[]
        {
            new MemoryModuleInfo("A", "V", "P1", 16UL * 1024 * 1024 * 1024, 6000, 4800),
            new MemoryModuleInfo("B", "V", "P2", 16UL * 1024 * 1024 * 1024, 6000, 4800),
            new MemoryModuleInfo("C", "V", "P3", 16UL * 1024 * 1024 * 1024, 6000, 6000)
        });

        var report = new RecommendationEngine().Analyze(Context(profile));

        var recommendation = Assert.Single(report.Recommendations.Where(r => r.Id == "rec.memory.below-rated-speed"));
        Assert.Equal(2, recommendation.Evidence.Count);
    }

    // ---- Scheduler priority separation -------------------------------------------------------

    [Fact]
    public void SchedulerRuleIsQuietWhenTheTweakIsAlreadyApplied()
    {
        var states = TestProfiles.AllNotApplied();
        states["system.win32-priority-separation"] = TweakState.Applied;

        var report = new RecommendationEngine().Analyze(Context(tweakStates: states));

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.system.scheduler-priority-separation");
    }

    [Fact]
    public void SchedulerRuleFiresWhenTheValueDeviatesFromClientDefault()
    {
        var states = TestProfiles.AllNotApplied();
        states["system.win32-priority-separation"] = TweakState.NotApplied;

        var report = new RecommendationEngine().Analyze(Context(tweakStates: states));

        var recommendation = Assert.Single(
            report.Recommendations.Where(r => r.Id == "rec.system.scheduler-priority-separation"));
        Assert.Equal("system.win32-priority-separation",
            ((RecommendedAction.ApplyTweak)recommendation.Action).TweakId);
    }

    [Fact]
    public void SchedulerRuleIsSkippedOnServerEditions()
    {
        var states = TestProfiles.AllNotApplied();
        states["system.win32-priority-separation"] = TweakState.NotApplied;
        var windows = TestProfiles.Windows(editionId: "ServerStandard");

        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(windows: windows), tweakStates: states));

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.system.scheduler-priority-separation");
        Assert.Contains(report.NotApplicable, outcome =>
            outcome.RuleId == "rec.system.scheduler-priority-separation" && outcome.Reason.Contains("Server"));
    }

    [Fact]
    public void SchedulerRuleIsUndeterminedWhenStateCannotBeRead()
    {
        var states = TestProfiles.AllNotApplied();
        states["system.win32-priority-separation"] = TweakState.Unknown;

        var report = new RecommendationEngine().Analyze(Context(tweakStates: states));

        Assert.Contains(report.Undetermined, outcome => outcome.RuleId == "rec.system.scheduler-priority-separation");
    }

    [Fact]
    public void AffinityIsSkippedWhenAPolicyIsAlreadySet()
    {
        var profile = TestProfiles.Profile();
        var existing = new[]
        {
            new HostControllerInfo
            {
                InstanceId = profile.UsbControllers[0].InstanceId,
                FriendlyName = profile.UsbControllers[0].FriendlyName,
                LogicalProcessorCount = profile.Cpu.LogicalProcessorCount,
                Policy = InterruptAffinityPolicy.SpecifiedProcessors,
                AffinityMask = 0b10
            }
        };

        var report = new RecommendationEngine().Analyze(Context(profile, affinityPolicies: existing));

        Assert.Contains(report.NotApplicable, outcome =>
            outcome.RuleId == "rec.interrupts.usb-controller-affinity");
    }

    [Fact]
    public void DriverLatencyStaysQuietBelowTheThreshold()
    {
        var context = Context(traces: new[] { Trace(topDriverMax: 120) });

        var report = new RecommendationEngine().Analyze(context);

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.drivers.worst-offender");
        Assert.Contains(report.NotApplicable, outcome => outcome.RuleId == "rec.drivers.worst-offender");
    }

    [Fact]
    public void DriverLatencyNamesTheOffenderAboveTheThreshold()
    {
        var context = Context(traces: new[] { Trace("rtwlane.sys", topDriverMax: 3200) });

        var report = new RecommendationEngine().Analyze(context);

        var recommendation = Assert.Single(report.Recommendations.Where(r => r.Id == "rec.drivers.worst-offender"));

        Assert.Contains("rtwlane.sys", recommendation.Title);
        Assert.Equal(RecommendationConfidence.Measured, recommendation.Confidence);
        Assert.IsType<RecommendedAction.ManualOnly>(recommendation.Action);
    }

    [Fact]
    public void WithoutATraceTheDriverRuleAsksForOne()
    {
        var report = new RecommendationEngine().Analyze(Context());

        Assert.Contains(report.Undetermined, outcome =>
            outcome.RuleId == "rec.drivers.worst-offender" && outcome.MissingInformation.Contains("trace"));
    }

    [Fact]
    public void MsiIsOnlySuggestedForDevicesWhereItIsWorthwhile()
    {
        var devices = new[]
        {
            new InterruptDeviceInfo(@"PCI\VEN_10DE&DEV_2486\0", "Test GPU", "GPU", IsMsiEnabled: false, InterruptPriority.Undefined),
            new InterruptDeviceInfo(@"PCI\VEN_1022&DEV_0001\0", "System timer", "System device", IsMsiEnabled: false, InterruptPriority.Undefined)
        };

        var report = new RecommendationEngine().Analyze(Context(interruptDevices: devices));

        var recommendation = Assert.Single(report.Recommendations.Where(r => r.Id == "rec.interrupts.enable-msi"));
        var action = Assert.IsType<RecommendedAction.EnableMsiMode>(recommendation.Action);

        // The system device must not be touched, however capable it claims to be.
        Assert.Equal(@"PCI\VEN_10DE&DEV_2486\0", action.InstanceId);
    }

    [Fact]
    public void MsiRuleWarnsThatADeviceCanFailToStart()
    {
        var devices = new[]
        {
            new InterruptDeviceInfo(@"PCI\VEN_10EC&DEV_8125\0", "Test NIC", "Network adapter", IsMsiEnabled: false, InterruptPriority.Undefined)
        };

        var report = new RecommendationEngine().Analyze(Context(interruptDevices: devices));

        var recommendation = Assert.Single(report.Recommendations.Where(r => r.Id == "rec.interrupts.enable-msi"));

        Assert.Contains("fail to start", recommendation.Risks);
        Assert.Contains(recommendation.Safety.Deductions, d => d.Contains("device"));
    }

    [Fact]
    public void LaptopsGetADifferentRiskStatementAndLowerImpactForProcessorState()
    {
        var desktop = new RecommendationEngine().Analyze(Context(TestProfiles.Profile(hasBattery: false)));
        var laptop = new RecommendationEngine().Analyze(Context(TestProfiles.Profile(hasBattery: true)));

        var desktopRule = desktop.Recommendations.Single(r => r.Id == "rec.cpu.min-processor-state");
        var laptopRule = laptop.Recommendations.Single(r => r.Id == "rec.cpu.min-processor-state");

        Assert.Contains("battery", laptopRule.Risks, StringComparison.OrdinalIgnoreCase);
        Assert.True(laptopRule.ImpactScore < desktopRule.ImpactScore);
    }

    [Fact]
    public void AppliedTweaksProduceNoRecommendation()
    {
        var report = new RecommendationEngine().Analyze(Context(tweakStates: TestProfiles.AllApplied()));

        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.power.high-performance-plan");
        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.cpu.disable-core-parking");
        Assert.DoesNotContain(report.Recommendations, r => r.Id == "rec.network.disable-nagle");
    }

    // ---- Conflicts -------------------------------------------------------------------------

    [Fact]
    public void VirtualMachineConflictsWithHardwareInterruptTuning()
    {
        var devices = new[]
        {
            new InterruptDeviceInfo(@"PCI\VEN_10DE&DEV_2486\0", "Test GPU", "GPU", IsMsiEnabled: false, InterruptPriority.Undefined)
        };

        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(windows: TestProfiles.Windows(virtualMachine: true)),
            interruptDevices: devices));

        var affinity = report.Recommendations.Single(r => r.Id == "rec.interrupts.usb-controller-affinity");

        Assert.True(affinity.HasBlockingConflict);
        Assert.Contains(affinity.Conflicts, conflict =>
            conflict.OtherRecommendationId == "rec.system.virtual-machine"
            && conflict.Kind == ConflictKind.MutuallyExclusive);
    }

    [Fact]
    public void ConflictsAreRecordedOnBothSides()
    {
        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(windows: TestProfiles.Windows(virtualMachine: true))));

        var vm = report.Recommendations.Single(r => r.Id == "rec.system.virtual-machine");
        var affinity = report.Recommendations.Single(r => r.Id == "rec.interrupts.usb-controller-affinity");

        Assert.Contains(vm.Conflicts, c => c.OtherRecommendationId == affinity.Id);
        Assert.Contains(affinity.Conflicts, c => c.OtherRecommendationId == vm.Id);
    }

    [Fact]
    public void PowerPlanOrderingConflictIsFlaggedAsInterference()
    {
        var report = new RecommendationEngine().Analyze(Context());

        var parking = report.Recommendations.Single(r => r.Id == "rec.cpu.disable-core-parking");

        var conflict = Assert.Single(parking.Conflicts.Where(c =>
            c.OtherRecommendationId == "rec.power.high-performance-plan"));

        // These can both be applied — the order just matters — so it must not block.
        Assert.Equal(ConflictKind.Interferes, conflict.Kind);
        Assert.False(parking.HasBlockingConflict);
    }

    [Fact]
    public void TwoAffinityRecommendationsSharingACoreConflict()
    {
        var detector = new ConflictDetector();
        var recommendations = new[]
        {
            BuildAffinityRecommendation("rec.test.a", @"PCI\A", new[] { 5 }),
            BuildAffinityRecommendation("rec.test.b", @"PCI\B", new[] { 5 })
        };

        detector.Annotate(recommendations);

        Assert.All(recommendations, recommendation =>
        {
            Assert.True(recommendation.HasBlockingConflict);
            Assert.Contains("logical processor 5", recommendation.Conflicts[0].Explanation);
        });
    }

    [Fact]
    public void AffinityRecommendationsOnDifferentCoresDoNotConflict()
    {
        var detector = new ConflictDetector();
        var recommendations = new[]
        {
            BuildAffinityRecommendation("rec.test.a", @"PCI\A", new[] { 5 }),
            BuildAffinityRecommendation("rec.test.b", @"PCI\B", new[] { 7 })
        };

        detector.Annotate(recommendations);

        Assert.All(recommendations, recommendation => Assert.Empty(recommendation.Conflicts));
    }

    private static Recommendation BuildAffinityRecommendation(string id, string instanceId, int[] cores) => new()
    {
        Id = id,
        Title = id,
        Category = RecommendationCategory.Interrupts,
        Reasoning = "test",
        ExpectedBenefit = "test",
        Risks = string.Empty,
        Evidence = new[] { new Evidence("test", "test") },
        Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
        Confidence = RecommendationConfidence.Inferred,
        Action = new RecommendedAction.SetInterruptAffinity(instanceId, cores),
        ImpactScore = 50
    };

    // ---- Ranking ---------------------------------------------------------------------------

    [Fact]
    public void MeasuredFindingsOutrankInferredOnes()
    {
        var report = new RecommendationEngine().Analyze(Context(traces: new[] { Trace(topDriverMax: 4000) }));

        var measuredIndex = report.Recommendations
            .Select((recommendation, index) => (recommendation, index))
            .First(pair => pair.recommendation.Confidence == RecommendationConfidence.Measured).index;

        var inferredIndex = report.Recommendations
            .Select((recommendation, index) => (recommendation, index))
            .First(pair => pair.recommendation.Confidence == RecommendationConfidence.Inferred).index;

        Assert.True(measuredIndex < inferredIndex);
    }

    [Fact]
    public void AutoApplicableExcludesManualDangerousAndConflictedItems()
    {
        var report = new RecommendationEngine().Analyze(Context(
            TestProfiles.Profile(windows: TestProfiles.Windows(memoryIntegrity: FeatureState.Enabled)),
            traces: new[] { Trace(topDriverMax: 4000) }));

        Assert.All(report.AutoApplicable, recommendation =>
        {
            Assert.IsType<RecommendedAction.ApplyTweak>(recommendation.Action);
            Assert.True(recommendation.Safety.IsAutoApplicable);
            Assert.False(recommendation.HasBlockingConflict);
            Assert.Empty(recommendation.Safety.Blockers);
        });

        Assert.DoesNotContain(report.AutoApplicable, r => r.Id == "rec.security.memory-integrity");
        Assert.DoesNotContain(report.AutoApplicable, r => r.Id == "rec.drivers.worst-offender");
    }

    [Fact]
    public void ReportIsTimestamped()
    {
        var before = DateTimeOffset.UtcNow;

        var report = new RecommendationEngine().Analyze(Context());

        Assert.InRange(report.GeneratedAt, before, DateTimeOffset.UtcNow);
    }
}
