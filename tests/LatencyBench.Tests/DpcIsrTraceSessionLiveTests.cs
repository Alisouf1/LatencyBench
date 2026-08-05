using System;
using System.Threading;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Elevation;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Starts a real ETW kernel session and collects real DPC/ISR events.
///
/// This exists because the DPC/ISR trace failed in the shipped build for a reason no unit test could
/// have caught: single-file publishing defines Assembly.Location as an empty string, so TraceEvent
/// built a nonexistent path when loading KernelTraceControl.dll and every trace died with
/// Win32Exception 126. The code was correct, the packaging was not — so the only test that can
/// prove the fix is one that actually opens the session.
///
/// Requires administrator rights (the NT Kernel Logger is elevation-only) and is therefore opt-in
/// via LATENCYBENCH_ETW=1. It reports rather than silently passing when it cannot run, so a skipped
/// run can never be mistaken for a verified one.
/// </summary>
public sealed class DpcIsrTraceSessionLiveTests
{
    private readonly ITestOutputHelper _output;

    public DpcIsrTraceSessionLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheEtwKernelSessionStartsAndDeliversRealSamples()
    {
        if (Environment.GetEnvironmentVariable("LATENCYBENCH_ETW") != "1")
        {
            _output.WriteLine("SKIPPED: set LATENCYBENCH_ETW=1 (and run elevated) to exercise the real ETW session.");
            return;
        }

        Assert.True(
            ElevationHelper.IsRunningAsAdministrator(),
            "This test must run elevated: the NT Kernel Logger session cannot be opened otherwise.");

        int samples = 0;
        double peakMicroseconds = 0;
        var kinds = new HashSet<DpcIsrKind>();

        using var session = new DpcIsrTraceSession();
        session.SampleReceived += sample =>
        {
            Interlocked.Increment(ref samples);
            kinds.Add(sample.Kind);
            if (sample.DurationMicroseconds > peakMicroseconds)
            {
                peakMicroseconds = sample.DurationMicroseconds;
            }
        };

        _output.WriteLine("Starting real ETW kernel session...");
        session.Start();

        Assert.True(session.IsRunning, "The session reported success but IsRunning is false.");
        _output.WriteLine("Session started. Collecting for 5 seconds...");

        Thread.Sleep(TimeSpan.FromSeconds(5));

        session.Stop();
        _output.WriteLine($"Stopped. samples={samples}, peak={peakMicroseconds:0.0}us, kinds={string.Join("+", kinds)}");

        // Any running Windows machine generates DPCs and ISRs constantly - timer interrupts alone
        // guarantee it. Zero samples over five seconds means the session opened but delivered
        // nothing, which is the distinct silent failure this test also has to catch.
        Assert.True(
            samples > 0,
            "The ETW session started but delivered zero DPC/ISR samples in 5 seconds. On a running " +
            "Windows machine that should be impossible, so the session is not actually collecting.");

        Assert.True(peakMicroseconds > 0, "Samples arrived but every duration was zero.");
    }
}
