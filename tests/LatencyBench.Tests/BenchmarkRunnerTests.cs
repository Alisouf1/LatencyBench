using System;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Benchmarking;
using LatencyBench.Core.Monitoring;

namespace LatencyBench.Tests;

public class BenchmarkRunnerTests
{
    /// <summary>Returns a fixed reading forever, and records how it was used.</summary>
    private sealed class FixedCounterSource : ICounterSource
    {
        private readonly (double Interrupt, double Dpc, double Processor, double AvailableMemory) _reading;

        public int PrimeCalls { get; private set; }

        public int SampleCalls { get; private set; }

        public bool Disposed { get; private set; }

        public FixedCounterSource((double, double, double, double) reading) => _reading = reading;

        public void Prime() => PrimeCalls++;

        public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
        {
            SampleCalls++;
            return (_reading.Interrupt, _reading.Dpc, _reading.Processor);
        }

        public double SampleAvailableMemoryPercent() => _reading.AvailableMemory;

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task CapturesTheFixedReadingAsBothAverageAndPeak()
    {
        var source = new FixedCounterSource((2.0, 3.0, 25.0, 60.0));
        var runner = new BenchmarkRunner(() => source);

        var snapshot = await runner.CaptureAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(3.0, snapshot.AverageDpcTimePercent, precision: 6);
        Assert.Equal(3.0, snapshot.PeakDpcTimePercent, precision: 6);
        Assert.Equal(2.0, snapshot.AverageInterruptTimePercent, precision: 6);
        Assert.Equal(60.0, snapshot.AverageAvailableMemoryPercent, precision: 6);
    }

    [Fact]
    public async Task PrimesBeforeTakingAnyReading()
    {
        var source = new FixedCounterSource((0, 0, 0, 100));
        var runner = new BenchmarkRunner(() => source);

        await runner.CaptureAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, source.PrimeCalls);
    }

    [Fact]
    public async Task DisposesTheCounterSourceAfterCapture()
    {
        var source = new FixedCounterSource((0, 0, 0, 100));
        var runner = new BenchmarkRunner(() => source);

        await runner.CaptureAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task OpensAFreshCounterSourcePerCapture()
    {
        int factoryCalls = 0;
        var runner = new BenchmarkRunner(() =>
        {
            factoryCalls++;
            return new FixedCounterSource((1, 1, 1, 90));
        });

        await runner.CaptureAsync(TimeSpan.FromMilliseconds(30));
        await runner.CaptureAsync(TimeSpan.FromMilliseconds(30));

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public async Task TakesAtLeastOneReadingEvenForAWindowShorterThanOneInterval()
    {
        var source = new FixedCounterSource((1, 1, 1, 90));
        var runner = new BenchmarkRunner(() => source);

        // Far shorter than BenchmarkRunner.SampleInterval (500 ms).
        var snapshot = await runner.CaptureAsync(TimeSpan.FromMilliseconds(1));

        Assert.True(snapshot.SampleCount >= 1);
    }

    [Fact]
    public async Task RecordsTheRequestedDuration()
    {
        var source = new FixedCounterSource((1, 1, 1, 90));
        var runner = new BenchmarkRunner(() => source);
        var requested = TimeSpan.FromMilliseconds(50);

        var snapshot = await runner.CaptureAsync(requested);

        Assert.Equal(requested, snapshot.Duration);
    }

    [Fact]
    public async Task CanBeCancelledMidCapture()
    {
        var source = new FixedCounterSource((1, 1, 1, 90));
        var runner = new BenchmarkRunner(() => source);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.CaptureAsync(TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void SnapshotBuilderRejectsAnEmptyReadingList()
    {
        Assert.Throws<ArgumentException>(() => LatencyBench.Core.Benchmarking.Models.BenchmarkSnapshot.FromReadings(
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), Array.Empty<(double, double, double, double)>()));
    }

    [Fact]
    public void SnapshotBuilderComputesAveragesAndPeaksCorrectly()
    {
        var readings = new (double Interrupt, double Dpc, double Processor, double AvailableMemory)[]
        {
            (1.0, 2.0, 10.0, 50.0),
            (3.0, 8.0, 30.0, 70.0),
            (2.0, 5.0, 20.0, 60.0)
        };

        var snapshot = LatencyBench.Core.Benchmarking.Models.BenchmarkSnapshot.FromReadings(
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), readings);

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(5.0, snapshot.AverageDpcTimePercent, precision: 6);
        Assert.Equal(8.0, snapshot.PeakDpcTimePercent, precision: 6);
        Assert.Equal(2.0, snapshot.AverageInterruptTimePercent, precision: 6);
        Assert.Equal(3.0, snapshot.PeakInterruptTimePercent, precision: 6);
        Assert.Equal(60.0, snapshot.AverageAvailableMemoryPercent, precision: 6);
    }

    // ---- Real counter source, smoke-tested against the actual machine ----------------------

    [Fact]
    public async Task RealCaptureAgainstTheMachineProducesASaneSnapshot()
    {
        var runner = new BenchmarkRunner();

        var snapshot = await runner.CaptureAsync(TimeSpan.FromSeconds(1));

        Assert.True(snapshot.SampleCount >= 1);
        Assert.InRange(snapshot.AverageDpcTimePercent, 0.0, 100.0);
        Assert.InRange(snapshot.PeakDpcTimePercent, 0.0, 100.0);
        Assert.InRange(snapshot.AverageAvailableMemoryPercent, 0.0, 100.0);
        Assert.True(snapshot.PeakDpcTimePercent >= snapshot.AverageDpcTimePercent);
    }
}
