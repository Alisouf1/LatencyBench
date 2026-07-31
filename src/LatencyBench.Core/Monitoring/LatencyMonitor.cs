using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.Core.Monitoring;

/// <summary>
/// Runs continuous background monitoring of interrupt time, DPC time and memory pressure, and
/// raises debounced warnings when one of them stays in a concerning range.
/// <para>
/// The thresholds below are heuristics, not values Microsoft publishes as pass/fail lines — no such
/// official line exists for aggregate DPC/interrupt percentages. They are deliberately conservative
/// (closer to "this is worth a look" than "this is definitely broken") because a monitor that cries
/// wolf gets ignored, and the DPC/ISR tab remains the tool for a precise, driver-level answer.
/// </para>
/// </summary>
public sealed class LatencyMonitor : IDisposable
{
	/// <summary>How often a sample is taken. Cheap enough — three performance counter reads and one
	/// kernel call — to run continuously without being a latency source itself.</summary>
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

	/// <summary>Consecutive samples over threshold before a warning is raised — filters a single
	/// spike, which is normal, from a sustained condition, which is not.</summary>
	private const int ConsecutiveToRaise = 3;

	private const int ConsecutiveToClear = 3;

	private readonly ICounterSource _counters;
	private readonly TimeSpan _interval;
	private readonly object _gate = new();

	private readonly MetricWatcher _dpcWatcher;
	private readonly MetricWatcher _interruptWatcher;
	private readonly MetricWatcher _memoryWatcher;

	private readonly List<MonitoringSample> _history = new();

	private CancellationTokenSource? _cancellation;
	private Task? _loop;

	public event Action<MonitoringSample>? SampleReceived;

	public event Action<MonitoringWarning>? WarningRaised;

	public bool IsRunning { get; private set; }

	/// <summary>The most recent samples, oldest first, bounded so memory use does not grow while the
	/// monitor runs for hours.</summary>
	public IReadOnlyList<MonitoringSample> History
	{
		get
		{
			lock (_gate)
			{
				return _history.ToArray();
			}
		}
	}

	private readonly int _maxHistory;

	public LatencyMonitor(ICounterSource? counters = null, TimeSpan? interval = null, int maxHistorySamples = 300)
	{
		_counters = counters ?? new PerformanceCounterSource();
		_interval = interval ?? DefaultInterval;
		_maxHistory = maxHistorySamples;

		_dpcWatcher = new MetricWatcher(
			WarningMetric.DpcTime,
			raiseThreshold: 5.0,
			clearThreshold: 3.0,
			ConsecutiveToRaise,
			ConsecutiveToClear,
			WarningSeverity.Warning,
			describeRaise: value =>
				$"DPC time has been elevated for a while (currently {value:0.0}%). A driver is holding the " +
				"processor in deferred procedure calls longer than usual — check the DPC/ISR tab to find which one.",
			describeClear: value => $"DPC time is back to normal ({value:0.0}%).");

		_interruptWatcher = new MetricWatcher(
			WarningMetric.InterruptTime,
			raiseThreshold: 10.0,
			clearThreshold: 6.0,
			ConsecutiveToRaise,
			ConsecutiveToClear,
			WarningSeverity.Warning,
			describeRaise: value =>
				$"Interrupt time has been elevated for a while (currently {value:0.0}%). A device is " +
				"generating interrupts faster than usual — a failing peripheral or a runaway driver are the " +
				"most common causes.",
			describeClear: value => $"Interrupt time is back to normal ({value:0.0}%).");

		// Fed 100 - available%, so higher is worse here too and the same raise/clear semantics apply.
		_memoryWatcher = new MetricWatcher(
			WarningMetric.MemoryPressure,
			raiseThreshold: 90.0,
			clearThreshold: 85.0,
			ConsecutiveToRaise,
			ConsecutiveToClear,
			WarningSeverity.Warning,
			describeRaise: pressure =>
				$"Available memory has been low for a while ({100 - pressure:0.0}% free). Under memory " +
				"pressure Windows pages more aggressively, which can show up as latency spikes unrelated to " +
				"interrupts entirely.",
			describeClear: pressure => $"Available memory has recovered ({100 - pressure:0.0}% free).");
	}

	public void Start()
	{
		lock (_gate)
		{
			if (IsRunning)
			{
				return;
			}

			_counters.Prime();
			_cancellation = new CancellationTokenSource();
			_loop = Task.Run(() => RunAsync(_cancellation.Token));
			IsRunning = true;
		}
	}

	public void Stop()
	{
		CancellationTokenSource? cancellation;
		lock (_gate)
		{
			if (!IsRunning)
			{
				return;
			}

			cancellation = _cancellation;
			IsRunning = false;
		}

		cancellation?.Cancel();
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				Sample();
			}
			catch (Exception)
			{
				// A single failed read — a counter category momentarily unavailable, for instance —
				// must not stop monitoring for the rest of the session.
			}

			try
			{
				await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
			}
			catch (TaskCanceledException)
			{
				break;
			}
		}
	}

	private void Sample()
	{
		var (interruptTime, dpcTime, processorTime) = _counters.SampleCpu();
		double availableMemoryPercent = _counters.SampleAvailableMemoryPercent();
		DateTimeOffset timestamp = DateTimeOffset.UtcNow;

		var sample = new MonitoringSample(timestamp, interruptTime, dpcTime, processorTime, availableMemoryPercent);

		lock (_gate)
		{
			_history.Add(sample);
			if (_history.Count > _maxHistory)
			{
				_history.RemoveAt(0);
			}
		}

		SampleReceived?.Invoke(sample);

		Emit(_dpcWatcher.Observe(dpcTime, timestamp));
		Emit(_interruptWatcher.Observe(interruptTime, timestamp));
		Emit(_memoryWatcher.Observe(100.0 - availableMemoryPercent, timestamp));
	}

	private void Emit(MonitoringWarning? warning)
	{
		if (warning is not null)
		{
			WarningRaised?.Invoke(warning);
		}
	}

	public void Dispose()
	{
		Stop();
		_loop?.Wait(TimeSpan.FromSeconds(5));
		_cancellation?.Dispose();
		_counters.Dispose();
	}
}
