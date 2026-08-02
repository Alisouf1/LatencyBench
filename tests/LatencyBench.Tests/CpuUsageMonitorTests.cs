using System;
using System.Linq;
using LatencyBench.Core.Perf;

namespace LatencyBench.Tests;

/// <summary>
/// The regression this suite exists for: SampleUsagePercent() used to return an all-zero array both
/// when the underlying NtQuerySystemInformation call failed AND when there was no prior sample to
/// diff against yet — indistinguishable from every core genuinely being idle. Callers (AffinityViewModel
/// and, through it, CoreAffinityAdvisor) could act on that as if it were a real reading.
/// </summary>
public class CpuUsageMonitorTests
{
    /// <summary>Lets a test force the failure path without needing to actually break the syscall on
    /// the test host, which — called with a correctly sized buffer — essentially always succeeds.</summary>
    private class ScriptedCpuUsageMonitor : CpuUsageMonitor
    {
        public bool ShouldFail { get; set; }

        protected override int QuerySystemInformation(nint buffer, int size) =>
            ShouldFail ? -1 : base.QuerySystemInformation(buffer, size);
    }

    [Fact]
    public void TheFirstSampleReturnsNullBecauseThereIsNoBaselineYet()
    {
        var monitor = new CpuUsageMonitor();

        Assert.Null(monitor.SampleUsagePercent());
    }

    [Fact]
    public void ASecondSampleAfterAWarmupReturnsRealUsage()
    {
        var monitor = new CpuUsageMonitor();
        monitor.SampleUsagePercent();

        var usage = monitor.SampleUsagePercent();

        Assert.NotNull(usage);
        Assert.Equal(Environment.ProcessorCount, usage!.Length);
        Assert.All(usage, u => Assert.InRange(u, 0.0, 100.0));
    }

    [Fact]
    public void AFailedReadReturnsNullNotZeros()
    {
        var monitor = new ScriptedCpuUsageMonitor { ShouldFail = true };

        Assert.Null(monitor.SampleUsagePercent());
    }

    [Fact]
    public void ASuccessAfterAFailureStillNeedsItsOwnWarmupRatherThanReusingAStaleBaseline()
    {
        // If the dropped-on-failure baseline were kept instead, this second (real) call could compute
        // a delta spanning more than one sampling interval without anything indicating that happened.
        var monitor = new ScriptedCpuUsageMonitor { ShouldFail = true };
        monitor.SampleUsagePercent();

        monitor.ShouldFail = false;
        var result = monitor.SampleUsagePercent();

        Assert.Null(result);
    }

    [Fact]
    public void RecoversToRealReadingsOnceGivenTwoConsecutiveSuccesses()
    {
        var monitor = new ScriptedCpuUsageMonitor { ShouldFail = true };
        monitor.SampleUsagePercent();

        monitor.ShouldFail = false;
        monitor.SampleUsagePercent(); // re-warms the baseline
        var usage = monitor.SampleUsagePercent();

        Assert.NotNull(usage);
        Assert.Equal(Environment.ProcessorCount, usage!.Length);
    }
}
