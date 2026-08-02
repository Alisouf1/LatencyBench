using System;
using LatencyBench.App.ViewModels;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Perf;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.Tests;

/// <summary>
/// The regression this suite exists for: RefreshCoreUsage() used to hand a failed CpuUsageMonitor
/// read straight to controller.Cores[*].UsagePercent and CoreAffinityAdvisor as if it were a real
/// all-idle reading, which could steer "which core is quietest" advice off wrong data with no sign
/// anything had failed to read.
/// </summary>
public sealed class AffinityViewModelTests
{
    /// <summary>Real readings are always clamped to [0, 100] by CpuUsageMonitor, so -1 can never occur
    /// from an actual sample — a safe sentinel for "RefreshCoreUsage did not touch this".</summary>
    private const double UntouchedSentinel = -1;

    private class ScriptedCpuUsageMonitor : CpuUsageMonitor
    {
        public bool ShouldFail { get; set; }

        protected override int QuerySystemInformation(nint buffer, int size) =>
            ShouldFail ? -1 : base.QuerySystemInformation(buffer, size);
    }

    private static HostControllerViewModel MakeController() => new(
        new HostControllerInfo { InstanceId = "USB\\TEST", FriendlyName = "Test Controller", LogicalProcessorCount = 2 },
        new InterruptAffinityService(),
        new DeviceRestartService(),
        Array.Empty<string>(),
        controllerNumber: 1);

    [Fact]
    public void AFailedCpuSampleLeavesCoreUsageAndAdviceUntouched()
    {
        var cpuMonitor = new ScriptedCpuUsageMonitor { ShouldFail = true };
        var vm = new AffinityViewModel(
            new InterruptAffinityService(),
            new DeviceRestartService(),
            new UsbTreeEnumerator(),
            new InterruptDeviceEnumerator(),
            cpuMonitor);
        var controller = MakeController();
        vm.Controllers.Add(controller);
        controller.Cores[0].UsagePercent = UntouchedSentinel;
        var adviceBefore = controller.CoreAdvice;

        vm.RefreshCoreUsage();

        Assert.Equal(UntouchedSentinel, controller.Cores[0].UsagePercent);
        Assert.Equal(adviceBefore, controller.CoreAdvice);
    }

    [Fact]
    public void ASuccessfulCpuSampleUpdatesCoreUsage()
    {
        var vm = new AffinityViewModel(
            new InterruptAffinityService(),
            new DeviceRestartService(),
            new UsbTreeEnumerator(),
            new InterruptDeviceEnumerator());
        var controller = MakeController();
        vm.Controllers.Add(controller);
        controller.Cores[0].UsagePercent = UntouchedSentinel;

        vm.RefreshCoreUsage(); // Warms up the real monitor's baseline — still null, still skipped.
        Assert.Equal(UntouchedSentinel, controller.Cores[0].UsagePercent);

        vm.RefreshCoreUsage(); // Now has a baseline — a real (non-null) reading.

        Assert.NotEqual(UntouchedSentinel, controller.Cores[0].UsagePercent);
        Assert.InRange(controller.Cores[0].UsagePercent, 0.0, 100.0);
    }

    [Fact]
    public void RepeatedFailedSamplesNeverProduceAReading()
    {
        var cpuMonitor = new ScriptedCpuUsageMonitor { ShouldFail = true };
        var vm = new AffinityViewModel(
            new InterruptAffinityService(),
            new DeviceRestartService(),
            new UsbTreeEnumerator(),
            new InterruptDeviceEnumerator(),
            cpuMonitor);
        var controller = MakeController();
        vm.Controllers.Add(controller);
        controller.Cores[0].UsagePercent = UntouchedSentinel;

        vm.RefreshCoreUsage();
        vm.RefreshCoreUsage();
        vm.RefreshCoreUsage();

        Assert.Equal(UntouchedSentinel, controller.Cores[0].UsagePercent);
        Assert.Equal("Collecting core usage data…", controller.CoreAdvice);
    }
}
