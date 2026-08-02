using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Monitoring;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.Tests;

public class LatencyMonitorTests
{
    /// <summary>Returns a scripted sequence of readings, one per call, holding the last value once
    /// the script runs out — so a test can drive the monitor through an exact scenario.</summary>
    private sealed class ScriptedCounterSource : ICounterSource
    {
        private readonly Queue<(double Interrupt, double Dpc, double Processor, double AvailableMemory)> _script;
        private (double Interrupt, double Dpc, double Processor, double AvailableMemory) _last = (0, 0, 0, 100);

        public int PrimeCalls { get; private set; }

        public ScriptedCounterSource(IEnumerable<(double Interrupt, double Dpc, double Processor, double AvailableMemory)> script)
        {
            _script = new Queue<(double, double, double, double)>(script);
        }

        public void Prime() => PrimeCalls++;

        public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
        {
            if (_script.Count > 0)
            {
                _last = _script.Dequeue();
            }

            return (_last.Interrupt, _last.Dpc, _last.Processor);
        }

        public double SampleAvailableMemoryPercent() => _last.AvailableMemory;

        public void Dispose()
        {
        }
    }

    private static async Task<(List<MonitoringSample> Samples, List<MonitoringWarning> Warnings)> RunAsync(
        ICounterSource source,
        int sampleCount,
        TimeSpan? interval = null)
    {
        var samples = new ConcurrentQueue<MonitoringSample>();
        var warnings = new ConcurrentQueue<MonitoringWarning>();

        using var monitor = new LatencyMonitor(source, interval ?? TimeSpan.FromMilliseconds(5));
        monitor.SampleReceived += samples.Enqueue;
        monitor.WarningRaised += warnings.Enqueue;

        monitor.Start();

        var deadline = Environment.TickCount64 + 10_000;
        while (samples.Count < sampleCount && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        monitor.Stop();

        return (new List<MonitoringSample>(samples), new List<MonitoringWarning>(warnings));
    }

    [Fact]
    public async Task PrimesTheCounterSourceBeforeSampling()
    {
        var source = new ScriptedCounterSource(new[] { (0.0, 0.0, 0.0, 100.0) });

        using var monitor = new LatencyMonitor(source, TimeSpan.FromMilliseconds(5));
        monitor.Start();
        await Task.Delay(50);
        monitor.Stop();

        Assert.Equal(1, source.PrimeCalls);
    }

    [Fact]
    public async Task EmitsASampleForEveryTick()
    {
        var script = new (double, double, double, double)[10];
        for (int i = 0; i < script.Length; i++)
        {
            script[i] = (1.0, 1.0, 20.0, 80.0);
        }

        var (samples, _) = await RunAsync(new ScriptedCounterSource(script), sampleCount: 5);

        Assert.True(samples.Count >= 5, $"Expected at least 5 samples, got {samples.Count}.");
        Assert.All(samples, s => Assert.Equal(1.0, s.DpcTimePercent));
    }

    [Fact]
    public async Task RaisesADpcWarningOnSustainedHighDpcTime()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 10; i++)
        {
            script.Add((0.0, 20.0, 30.0, 80.0)); // DPC time well above the 5% threshold
        }

        var (_, warnings) = await RunAsync(new ScriptedCounterSource(script), sampleCount: 6);

        Assert.Contains(warnings, w => w.Metric == WarningMetric.DpcTime && !w.IsRecovery);
    }

    [Fact]
    public async Task RaisesAMemoryWarningWhenAvailableMemoryStaysLow()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 10; i++)
        {
            script.Add((0.0, 0.0, 10.0, 5.0)); // 5% available — well under the 10% floor
        }

        var (_, warnings) = await RunAsync(new ScriptedCounterSource(script), sampleCount: 6);

        Assert.Contains(warnings, w => w.Metric == WarningMetric.MemoryPressure && !w.IsRecovery);
    }

    [Fact]
    public async Task DoesNotWarnWhenEverythingIsNormal()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 10; i++)
        {
            script.Add((1.0, 1.0, 15.0, 70.0));
        }

        var (_, warnings) = await RunAsync(new ScriptedCounterSource(script), sampleCount: 6);

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task RecoversAfterReturningToNormal()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 6; i++)
        {
            script.Add((0.0, 20.0, 30.0, 80.0)); // raise
        }
        for (int i = 0; i < 10; i++)
        {
            script.Add((0.0, 1.0, 15.0, 80.0)); // clear
        }

        var (_, warnings) = await RunAsync(new ScriptedCounterSource(script), sampleCount: 14);

        Assert.Contains(warnings, w => w.Metric == WarningMetric.DpcTime && !w.IsRecovery);
        Assert.Contains(warnings, w => w.Metric == WarningMetric.DpcTime && w.IsRecovery);
    }

    [Fact]
    public void HistoryIsEmptyBeforeStarting()
    {
        using var monitor = new LatencyMonitor(new ScriptedCounterSource(Array.Empty<(double, double, double, double)>()));

        Assert.Empty(monitor.History);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task HistoryIsBoundedSoItDoesNotGrowWithoutLimit()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 30; i++)
        {
            script.Add((1.0, 1.0, 10.0, 80.0));
        }

        using var monitor = new LatencyMonitor(
            new ScriptedCounterSource(script),
            interval: TimeSpan.FromMilliseconds(2),
            maxHistorySamples: 5);

        monitor.Start();

        var deadline = Environment.TickCount64 + 5000;
        while (monitor.History.Count < 5 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        monitor.Stop();
        await Task.Delay(50);

        Assert.True(monitor.History.Count <= 5, $"History grew to {monitor.History.Count}, expected a cap of 5.");
    }

    [Fact]
    public async Task StoppingPreventsFurtherSamples()
    {
        var script = new List<(double, double, double, double)>();
        for (int i = 0; i < 50; i++)
        {
            script.Add((1.0, 1.0, 10.0, 80.0));
        }

        using var monitor = new LatencyMonitor(new ScriptedCounterSource(script), TimeSpan.FromMilliseconds(5));

        monitor.Start();
        await Task.Delay(50);
        monitor.Stop();

        int countAfterStop = monitor.History.Count;
        await Task.Delay(100);

        Assert.Equal(countAfterStop, monitor.History.Count);
    }

    [Fact]
    public void StartIsIdempotent()
    {
        using var monitor = new LatencyMonitor(
            new ScriptedCounterSource(Array.Empty<(double, double, double, double)>()),
            TimeSpan.FromMilliseconds(50));

        monitor.Start();
        monitor.Start();

        Assert.True(monitor.IsRunning);
    }

    [Fact]
    public void StopBeforeStartIsANoOp()
    {
        using var monitor = new LatencyMonitor(
            new ScriptedCounterSource(Array.Empty<(double, double, double, double)>()));

        monitor.Stop();

        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task AFailingCounterReadDoesNotStopMonitoring()
    {
        var source = new ThrowingCounterSource(throwFirstNCalls: 3);

        using var monitor = new LatencyMonitor(source, TimeSpan.FromMilliseconds(5));
        var samples = new ConcurrentQueue<MonitoringSample>();
        monitor.SampleReceived += samples.Enqueue;

        monitor.Start();

        var deadline = Environment.TickCount64 + 5000;
        while (samples.Count < 2 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        monitor.Stop();

        Assert.True(samples.Count >= 2, "Monitoring should continue past a transient read failure.");
    }

    private sealed class ThrowingCounterSource : ICounterSource
    {
        private readonly int _throwFirstNCalls;
        private int _calls;

        public ThrowingCounterSource(int throwFirstNCalls) => _throwFirstNCalls = throwFirstNCalls;

        public void Prime()
        {
        }

        public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
        {
            _calls++;
            if (_calls <= _throwFirstNCalls)
            {
                throw new InvalidOperationException("Simulated transient counter failure.");
            }

            return (1.0, 1.0, 10.0);
        }

        public double SampleAvailableMemoryPercent() => 80.0;

        public void Dispose()
        {
        }
    }

    // ---- Real counter source, smoke-tested against the actual machine ----------------------

    [Fact]
    public void RealCounterSourceReturnsSaneMemoryPercent()
    {
        using var source = new PerformanceCounterSource();

        double available = source.SampleAvailableMemoryPercent();

        Assert.InRange(available, 0.0, 100.0);
    }

    [Fact]
    public void RealCounterSourceReturnsSaneCpuPercentagesAfterPriming()
    {
        using var source = new PerformanceCounterSource();
        source.Prime();
        Thread.Sleep(100);

        var (interrupt, dpc, processor) = source.SampleCpu();

        Assert.InRange(interrupt, 0.0, 100.0);
        Assert.InRange(dpc, 0.0, 100.0);
        Assert.InRange(processor, 0.0, 100.0);
    }
}
