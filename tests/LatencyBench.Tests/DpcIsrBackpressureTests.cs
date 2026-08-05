using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.DpcIsr;

namespace LatencyBench.Tests;

/// <summary>
/// Stress-tests the bounded hand-off between the ETW producer and the UI drain timer.
///
/// The producer runs on the ETW processing thread at kernel interrupt rates; the consumer is a 150 ms
/// timer on the UI thread. Any UI stall therefore lets the queue grow, and unbounded that is an
/// out-of-memory path on precisely the busy machine someone would be tracing.
///
/// These exercise the same increment-check-decrement discipline the view model uses, at a rate and
/// concurrency level a real trace cannot reach, because the view model itself needs a WPF dispatcher
/// and a live kernel session to run.
/// </summary>
public sealed class DpcIsrBackpressureTests
{
    private const int MaxPending = 200_000;

    /// <summary>Mirrors the view model's producer/consumer pair exactly.</summary>
    private sealed class BoundedSink
    {
        private readonly ConcurrentQueue<DpcIsrSample> _pending = new();
        private int _pendingCount;
        private int _dropped;

        public int Dropped => Volatile.Read(ref _dropped);

        public int Depth => Volatile.Read(ref _pendingCount);

        public int QueuedItems => _pending.Count;

        public void Produce(DpcIsrSample sample)
        {
            if (Interlocked.Increment(ref _pendingCount) > MaxPending)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _dropped);
                return;
            }

            _pending.Enqueue(sample);
        }

        public int Drain()
        {
            int drained = 0;
            while (_pending.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingCount);
                drained++;
            }

            return drained;
        }

        public void Reset()
        {
            _pending.Clear();
            Interlocked.Exchange(ref _pendingCount, 0);
            Interlocked.Exchange(ref _dropped, 0);
        }
    }

    private static DpcIsrSample Sample() => new()
    {
        Timestamp = DateTime.UtcNow,
        Kind = DpcIsrKind.Dpc,
        DurationMicroseconds = 12.5,
        ProcessorNumber = 0,
    };

    [Fact]
    public void AFloodWithNoConsumerIsCappedRatherThanGrowingWithoutBound()
    {
        var sink = new BoundedSink();

        for (int i = 0; i < MaxPending * 3; i++)
        {
            sink.Produce(Sample());
        }

        Assert.Equal(MaxPending, sink.Depth);
        Assert.Equal(MaxPending * 3 - MaxPending, sink.Dropped);
    }

    [Fact]
    public void NothingIsDroppedWhileTheConsumerKeepsUp()
    {
        var sink = new BoundedSink();
        int drained = 0;

        for (int batch = 0; batch < 50; batch++)
        {
            for (int i = 0; i < 1_000; i++)
            {
                sink.Produce(Sample());
            }

            drained += sink.Drain();
        }

        Assert.Equal(0, sink.Dropped);
        Assert.Equal(50_000, drained);
        Assert.Equal(0, sink.Depth);
    }

    [Fact]
    public async Task DepthAndQueueStayConsistentUnderConcurrentProducersAndOneConsumer()
    {
        // The invariant that matters: the tracked depth must never drift from the real queue length,
        // or the ceiling stops meaning anything. Eight producers is well beyond the single ETW
        // callback thread the app actually has.
        var sink = new BoundedSink();
        using var stop = new CancellationTokenSource();

        Task[] producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                sink.Produce(Sample());
            }
        })).ToArray();

        Task consumer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                sink.Drain();
                await Task.Delay(5);
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Task.WhenAll(producers.Append(consumer));

        sink.Drain();

        Assert.Equal(0, sink.Depth);
        Assert.Equal(0, sink.QueuedItems);
    }

    [Fact]
    public void TheCeilingIsNeverExceededUnderConcurrentProducers()
    {
        var sink = new BoundedSink();

        Parallel.For(0, MaxPending * 2, _ => sink.Produce(Sample()));

        // Concurrent producers can momentarily overshoot between the increment and the decrement, so
        // the guarantee is a bound, not an exact figure. What must hold is that it stays within a
        // small margin rather than growing without limit.
        Assert.InRange(sink.Depth, MaxPending - 1_000, MaxPending + 1_000);
        Assert.True(sink.Dropped > 0, "a flood of twice the ceiling must drop something");
    }

    [Fact]
    public void ResetClearsDepthDropsAndBacklogTogether()
    {
        // Start() resets these three as a unit; leaving any behind carries a previous trace's data
        // into the next one.
        var sink = new BoundedSink();
        for (int i = 0; i < MaxPending + 5_000; i++)
        {
            sink.Produce(Sample());
        }

        Assert.True(sink.Dropped > 0);

        sink.Reset();

        Assert.Equal(0, sink.Depth);
        Assert.Equal(0, sink.Dropped);
        Assert.Equal(0, sink.QueuedItems);
    }

    [Fact]
    public void ProducingRemainsCheapWhenTheQueueIsFull()
    {
        // Once saturated the producer must stay O(1). This runs on the ETW callback path, so anything
        // that degrades as the backlog grows would slow the very tracing it is measuring.
        var sink = new BoundedSink();
        for (int i = 0; i < MaxPending; i++)
        {
            sink.Produce(Sample());
        }

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 500_000; i++)
        {
            sink.Produce(Sample());
        }

        stopwatch.Stop();

        Assert.Equal(MaxPending, sink.Depth);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"500k rejected produces took {stopwatch.Elapsed.TotalSeconds:0.0}s - the reject path is not O(1).");
    }
}
