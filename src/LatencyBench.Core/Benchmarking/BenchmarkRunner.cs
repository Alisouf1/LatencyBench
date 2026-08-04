using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Benchmarking.Models;
using LatencyBench.Core.Monitoring;

namespace LatencyBench.Core.Benchmarking;

/// <summary>
/// Captures a short, automatic before/after measurement around an applied change.
/// <para>
/// This exists because the two measurement tools the app already has both need something the
/// optimisation flow cannot guarantee: the DPC/ISR tab needs the single system-wide ETW kernel
/// session, which may already be in use, and the port test needs a human physically moving a mouse
/// or typing for its whole duration. Reusing the same lightweight performance counters the Monitor
/// tab reads gives every applied change a measurement automatically, with neither constraint — at
/// the cost of only ever reporting an aggregate percentage, never a driver name. A user who wants
/// that precision still has the DPC/ISR tab; this is what makes "measure before/after" a default
/// instead of a manual extra step.
/// </para>
/// </summary>
public sealed class BenchmarkRunner
{
	/// <summary>How often a reading is taken during a capture window.</summary>
	public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

	/// <summary>Default capture length. Long enough to average out a couple of outlier samples,
	/// short enough that measuring twice does not make applying a profile feel slow.</summary>
	public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(8);

	private readonly Func<ICounterSource> _counterSourceFactory;

	public BenchmarkRunner(Func<ICounterSource>? counterSourceFactory = null)
	{
		_counterSourceFactory = counterSourceFactory ?? (() => new PerformanceCounterSource());
	}

	/// <summary>
	/// Captures one snapshot. A fresh counter source is opened and disposed for each call rather than
	/// reused across a whole before/after pair — the machine's state changes between the two calls
	/// (that is the entire point), and the rate counters need re-priming regardless, so there is no
	/// benefit to holding the handles open across the gap.
	/// </summary>
	public async Task<BenchmarkSnapshot> CaptureAsync(
		TimeSpan? duration = null,
		IProgress<TimeSpan>? progress = null,
		CancellationToken cancellationToken = default)
	{
		TimeSpan window = duration ?? DefaultDuration;
		DateTimeOffset startedAt = DateTimeOffset.UtcNow;

		using ICounterSource source = _counterSourceFactory();
		source.Prime();

		// The rate counters' first real read needs at least one interval to have elapsed since
		// priming, otherwise it reports the average since process start rather than since now.
		await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);

		var readings = new List<(double Interrupt, double Dpc, double Processor, double AvailableMemory)>();
		DateTime deadline = DateTime.UtcNow + window;

		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var (interrupt, dpc, processor) = source.SampleCpu();
			double availableMemory = source.SampleAvailableMemoryPercent();
			readings.Add((interrupt, dpc, processor, availableMemory));

			progress?.Report(deadline - DateTime.UtcNow);

			await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
		}

		// The loop can exit with zero readings if the window is shorter than one sample interval —
		// take one final reading rather than failing to build a snapshot at all.
		if (readings.Count == 0)
		{
			var (interrupt, dpc, processor) = source.SampleCpu();
			readings.Add((interrupt, dpc, processor, source.SampleAvailableMemoryPercent()));
		}

		return BenchmarkSnapshot.FromReadings(startedAt, window, readings);
	}
}
