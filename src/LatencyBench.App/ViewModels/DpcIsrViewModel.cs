using LatencyBench.Core.Diagnostics;
using System.Threading;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;

namespace LatencyBench.App.ViewModels;

public sealed partial class DpcIsrViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 40;

    /// <summary>Matches the thresholds the analysis and severity colours use.</summary>
    private const double HighSpikeMicroseconds = 500;
    private const double BorderlineSpikeMicroseconds = 100;

    private readonly DpcIsrTraceSession _session = new();

    /// <summary>
    /// Hand-off from the ETW processing thread to the UI drain timer.
    /// <para>
    /// Shared state: enqueued by the single ETW callback thread, dequeued only by
    /// <see cref="DrainPending"/> on the UI thread. <see cref="_pendingCount"/> and
    /// <see cref="_droppedSamples"/> are maintained with interlocked operations because they are
    /// touched from both. No lock is held across either side.
    /// </para>
    /// </summary>
    private readonly ConcurrentQueue<DpcIsrSample> _pending = new();

    /// <summary>
    /// Cap on unconsumed samples. The producer runs at kernel interrupt rates while the consumer is a
    /// 150 ms timer on the UI thread, so any UI stall - a modal dialog, a slow render, another tab's
    /// work - lets the queue grow without limit. Left unbounded this is an out-of-memory path on
    /// exactly the busy machine a user would be tracing. At roughly 64 bytes per sample this ceiling
    /// is about 13 MB, and around four seconds of headroom even at 50k events per second.
    /// </summary>
    private const int MaxPendingSamples = 200_000;

    /// <summary>Current queue depth. Tracked separately because ConcurrentQueue.Count takes a
    /// snapshot across segments, which is not something to pay for on every interrupt.</summary>
    private int _pendingCount;

    /// <summary>Samples discarded because the queue was full. Surfaced in the result rather than
    /// hidden: dropped events understate the spike counts, so a trace that lost data must say so
    /// instead of quietly reporting a better number than the machine earned.</summary>
    private int _droppedSamples;
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DpcIsrHistoryStore _historyStore;
    private readonly InterruptDeviceEnumerator _deviceEnumerator;
    private readonly Dictionary<string, (double MaxMicroseconds, int Count)> _driverStats = [];
    private readonly Dictionary<int, int> _coreEventCounts = [];
    private int _dpcSampleCount;
    private int _isrSampleCount;

    /// <summary>Spike counters + trace start, so severity can be reported as a rate. "Highest DPC" only
    /// ever climbs, so a longer trace always reports a bigger number — it cannot be compared between
    /// runs, which is exactly what someone A/B testing a setting needs to do.</summary>
    private int _highSpikeCount;
    private int _borderlineSpikeCount;
    private DateTime _traceStartedAt;

    public ObservableCollection<DpcIsrSample> RecentDurations { get; } = [];

    public ObservableCollection<DriverStatViewModel> TopDrivers { get; } = [];

    /// <summary>Which CPU cores the traced interrupts actually landed on — ties this tab to core affinity.</summary>
    public ObservableCollection<CoreInterruptLoadViewModel> CoreLoads { get; } = [];

    [ObservableProperty]
    private bool _isTracing;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private double _highestDpcMicroseconds;

    [ObservableProperty]
    private double _highestIsrMicroseconds;

    [ObservableProperty]
    private long _sampleCount;

    [ObservableProperty]
    private string _analysisMessage = "Start tracing, let it run for a few seconds, then stop it — the analysis and a suggested fix (if any) will appear here.";

    /// <summary>Spikes over 500 µs per second. Unlike "highest DPC", this doesn't grow with trace length, so it's the number to compare between two settings.</summary>
    [ObservableProperty]
    private double _highSpikesPerSecond;

    /// <summary>Spikes over 100 µs per second.</summary>
    [ObservableProperty]
    private double _borderlineSpikesPerSecond;

    [ObservableProperty]
    private double _traceSeconds;

    /// <summary>Fixed trace lengths. A fixed length is what makes two traces comparable at all — an
    /// open-ended trace just accumulates a bigger peak the longer it runs.</summary>
    public static IReadOnlyList<int> AvailableDurations { get; } = [10, 30, 60, 120];

    [ObservableProperty]
    private int _selectedDuration = 30;

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private bool _isSaved;

    [ObservableProperty]
    private string? _comparisonMessage;

    [ObservableProperty]
    private DpcIsrTestResult? _lastResult;

    public ObservableCollection<DpcIsrTestResult> SavedTraces { get; }

    public DpcIsrViewModel(DpcIsrHistoryStore historyStore, InterruptDeviceEnumerator deviceEnumerator)
    {
        _historyStore = historyStore;
        _deviceEnumerator = deviceEnumerator;
        SavedTraces = historyStore.Results;

        // Runs on the ETW processing thread at interrupt rates. Bounded rather than unbounded: see
        // MaxPendingSamples. Increment-then-check-then-decrement keeps this lock-free; the ceiling can
        // be overshot momentarily by concurrent producers, which is harmless for a backpressure limit.
        _session.SampleReceived += sample =>
        {
            if (Interlocked.Increment(ref _pendingCount) > MaxPendingSamples)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _droppedSamples);
                return;
            }

            _pending.Enqueue(sample);
        };

        // ETW can fire hundreds of events per second on a busy system — draining on a timer
        // instead of dispatching to the UI thread per-sample keeps the UI responsive.
        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _drainTimer.Tick += (_, _) => DrainPending();

        // Auto-stops at the chosen length. Fixed-length traces are the whole point: two traces of
        // different lengths can't be compared, because the peak keeps climbing the longer you run.
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            SecondsRemaining--;
            if (SecondsRemaining <= 0)
            {
                Stop();
            }
        };
    }

    /// <summary>Resolved when a RunOnceAsync-started trace finishes (naturally, via the countdown, or manually via Stop) — lets an external orchestrator await one full trace instead of driving the timer itself.</summary>
    private TaskCompletionSource<bool>? _completionSource;

    [RelayCommand]
    private void Toggle()
    {
        if (IsTracing)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>
    /// Starts a fixed-length trace and returns a task that completes when it stops — for a caller
    /// (the "run everything" orchestrator) that needs to wait for the whole trace rather than drive
    /// the UI's own start/stop timer. A no-op returning a completed task if a trace is already
    /// running, since starting a second one would just be ignored by Start() anyway.
    /// </summary>
    public Task RunOnceAsync(int durationSeconds)
    {
        if (IsTracing)
        {
            return Task.CompletedTask;
        }

        SelectedDuration = durationSeconds;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completionSource = tcs;
        Start();

        if (!IsTracing)
        {
            // Start() swallows its own failures (e.g. another NT Kernel Logger session already
            // holds the trace) and only ever surfaces them via StatusMessage — without this check
            // the caller would await a Stop() that can now never come, and hang forever.
            _completionSource = null;
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }

    private void Start()
    {
        DiagnosticLog.Info("DpcIsr", $"Start requested. duration={SelectedDuration}s, sessionAlreadyRunning={_session.IsRunning}.");

        try
        {
            _session.Start();
            DiagnosticLog.Info("DpcIsr", "ETW kernel session started successfully.");

            // Every other accumulator was reset here but the hand-off queue was not, so any sample
            // still in flight when the previous trace stopped - ETW flushes its buffers on Stop, and
            // a late callback can land after the final drain - was counted against this trace instead.
            // Cleared with the counters it feeds so a trace always starts from nothing.
            _pending.Clear();
            Interlocked.Exchange(ref _pendingCount, 0);
            Interlocked.Exchange(ref _droppedSamples, 0);

            _driverStats.Clear();
            _coreEventCounts.Clear();
            TopDrivers.Clear();
            CoreLoads.Clear();
            HighestDpcMicroseconds = 0;
            HighestIsrMicroseconds = 0;
            SampleCount = 0;
            _dpcSampleCount = 0;
            _isrSampleCount = 0;
            _highSpikeCount = 0;
            _borderlineSpikeCount = 0;
            _traceStartedAt = DateTime.Now;
            HighSpikesPerSecond = 0;
            BorderlineSpikesPerSecond = 0;
            TraceSeconds = 0;
            RecentDurations.Clear();
            IsSaved = false;
            LastResult = null;
            ComparisonMessage = null;
            SecondsRemaining = SelectedDuration;
            AnalysisMessage = $"Tracing for {SelectedDuration}s — go and do the thing that stutters. It stops on its own.";
            IsTracing = true;
            StatusMessage = "Tracing live DPC/ISR activity…";
            _drainTimer.Start();
            _countdownTimer.Start();
        }
        catch (Exception ex)
        {
            // The whole failure, not just ex.Message: the message alone for an ETW start failure is
            // routinely something like "Access is denied" with no indication of which of the several
            // possible causes it was, and this is the exact path a user reports as "trace does nothing".
            DiagnosticLog.Error("DpcIsr", "ETW kernel session FAILED to start.", ex);
            StatusMessage = ex.Message;
        }
    }

    private void Stop()
    {
        DiagnosticLog.Info(
            "DpcIsr",
            $"Stop requested. samples={SampleCount}, dpc={_dpcSampleCount}, isr={_isrSampleCount}, " +
            $"elapsed={TraceSeconds:0}s.");

        _drainTimer.Stop();
        _countdownTimer.Stop();

        // Order matters: stopping the session is what flushes ETW's buffers, and it delivers the
        // events it had been holding (measured at up to ~1.2 s worth on real hardware) before it
        // returns. Draining first and stopping second silently threw all of those away — the final
        // second of every trace was missing from the totals, the peaks, and the analysis.
        _session.Stop();
        DrainPending();

        IsTracing = false;
        StatusMessage = "Stopped.";

        // A trace that ran but collected nothing is a distinct, silent failure mode from one that
        // never started: the session opened, the timer ran out, and the user is shown an empty result
        // with no error. Recorded explicitly so the two cannot be confused in a bug report.
        if (SampleCount == 0)
        {
            DiagnosticLog.Warn(
                "DpcIsr",
                "Trace completed but captured ZERO samples. The ETW session started without error yet " +
                "delivered no DPC/ISR events.");
        }
        else
        {
            DiagnosticLog.Info(
                "DpcIsr",
                $"Trace complete: {SampleCount} sample(s), peak DPC {HighestDpcMicroseconds}us, " +
                $"peak ISR {HighestIsrMicroseconds}us, {_driverStats.Count} driver(s) resolved, " +
                $"{Volatile.Read(ref _droppedSamples)} dropped.");
        }

        int dropped = Volatile.Read(ref _droppedSamples);
        if (dropped > 0)
        {
            // Stated plainly rather than buried: dropped events understate every count below, so the
            // numbers are a floor, not a measurement. Saying nothing would make this trace look
            // better than the machine actually behaved.
            DiagnosticLog.Warn("DpcIsr", $"{dropped} sample(s) were dropped because the queue was full.");
            AnalysisMessage =
                $"⚠ {dropped:N0} event(s) were dropped because they arrived faster than they could be " +
                "processed. The figures below are therefore a lower bound — the real spike counts are " +
                "higher. This happens when the machine is extremely busy, which is itself a finding." +
                Environment.NewLine + Environment.NewLine + AnalysisMessage;
        }

        var driverStats = _driverStats
            .Select(kv => new DriverStat(kv.Key, kv.Value.MaxMicroseconds, kv.Value.Count))
            .ToList();
        var coreLoads = CoreLoads.Select(c => new CoreLoad(c.CoreIndex, c.SharePercent)).ToList();
        AnalysisMessage = DpcIsrAnalysisGenerator.Generate(
            driverStats, HighestDpcMicroseconds, HighestIsrMicroseconds, _dpcSampleCount, _isrSampleCount, coreLoads)
            + $"\n\nComparable figure: {HighSpikesPerSecond:0.0} spikes over 500 µs per second, {BorderlineSpikesPerSecond:0.0} over 100 µs per second, across {TraceSeconds:0} s. " +
              "Use these when comparing two settings — the peaks above only climb with trace length and can't be compared across runs.";

        BuildResult();

        _completionSource?.TrySetResult(true);
        _completionSource = null;
    }

    private void DrainPending()
    {
        // Accumulate into locals rather than touching the observable properties per sample: a busy
        // system produces thousands of events per 150 ms window, and notifying per sample floods
        // WPF with change notifications for values nobody can read that fast.
        DpcIsrSample? peak = null;
        var dpcCount = _dpcSampleCount;
        var isrCount = _isrSampleCount;
        var maxDpc = HighestDpcMicroseconds;
        var maxIsr = HighestIsrMicroseconds;

        while (_pending.TryDequeue(out var sample))
        {
            Interlocked.Decrement(ref _pendingCount);

            if (sample.Kind == DpcIsrKind.Dpc)
            {
                dpcCount++;
                maxDpc = Math.Max(maxDpc, sample.DurationMicroseconds);
            }
            else
            {
                isrCount++;
                maxIsr = Math.Max(maxIsr, sample.DurationMicroseconds);
            }

            if (peak is null || sample.DurationMicroseconds > peak.DurationMicroseconds)
            {
                peak = sample;
            }

            if (sample.DurationMicroseconds >= HighSpikeMicroseconds)
            {
                _highSpikeCount++;
            }

            if (sample.DurationMicroseconds >= BorderlineSpikeMicroseconds)
            {
                _borderlineSpikeCount++;
            }

            var existing = _driverStats.TryGetValue(sample.DriverName, out var stat) ? stat : (0.0, 0);
            _driverStats[sample.DriverName] = (Math.Max(existing.Item1, sample.DurationMicroseconds), existing.Item2 + 1);

            _coreEventCounts[sample.ProcessorNumber] = _coreEventCounts.GetValueOrDefault(sample.ProcessorNumber) + 1;
        }

        if (peak is null)
        {
            return;
        }

        _dpcSampleCount = dpcCount;
        _isrSampleCount = isrCount;
        SampleCount = (long)dpcCount + isrCount;
        HighestDpcMicroseconds = maxDpc;
        HighestIsrMicroseconds = maxIsr;

        TraceSeconds = (DateTime.Now - _traceStartedAt).TotalSeconds;
        if (TraceSeconds > 0)
        {
            HighSpikesPerSecond = _highSpikeCount / TraceSeconds;
            BorderlineSpikesPerSecond = _borderlineSpikeCount / TraceSeconds;
        }

        // One bar per drain tick, showing that window's worst event, so the strip reads as a rolling
        // ~6 s timeline of peak latency (the shape LatencyMon shows). Plotting the last 40 raw
        // events instead is meaningless at thousands of events per second — it's a sub-10 ms
        // snapshot that churns far too fast to see.
        RecentDurations.Add(peak);
        while (RecentDurations.Count > MaxHistory)
        {
            RecentDurations.RemoveAt(0);
        }

        RefreshTopDrivers();
        RefreshCoreLoads();
    }

    /// <summary>
    /// Packages the finished trace, capturing the GPU's current MSI/priority settings alongside it so
    /// the saved run records the config it was taken under — no need to remember what was changed.
    /// </summary>
    private void BuildResult()
    {
        if (TraceSeconds <= 0 || _driverStats.Count == 0)
        {
            return;
        }

        var top = _driverStats.OrderByDescending(kv => kv.Value.MaxMicroseconds).First();

        LastResult = new DpcIsrTestResult
        {
            DurationSeconds = TraceSeconds,
            HighSpikesPerSecond = HighSpikesPerSecond,
            BorderlineSpikesPerSecond = BorderlineSpikesPerSecond,
            HighestDpcMicroseconds = HighestDpcMicroseconds,
            HighestIsrMicroseconds = HighestIsrMicroseconds,
            TopDriverName = top.Key,
            TopDriverMaxMicroseconds = top.Value.MaxMicroseconds,
            TunedDevices = CaptureTunableConfigs(),
            // Saved with the run so the Affinity tab can average across many traces rather than
            // re-deciding from whichever one finished last.
            CoreLoads = CoreLoads.Select(c => new CoreLoad(c.CoreIndex, c.SharePercent)).ToList(),
        };

        StatusMessage = $"Done — {TraceSeconds:0}s traced. Click \"Save trace\" to compare it against other settings.";
    }

    /// <summary>Every category the app actually gives MSI/IRQ advice about — see InterruptDeviceAdvisor, which recommends High priority for USB controllers and audio adapters specifically.</summary>
    private static readonly HashSet<string> TrackedConfigCategories =
        new(StringComparer.OrdinalIgnoreCase) { "GPU", "USB controller", "Audio controller" };

    /// <summary>
    /// Snapshots the interrupt settings of every device whose configuration the comparison needs to
    /// tell apart. All of them, ordered by name, so the resulting label is identical for two traces
    /// taken under identical settings — that stability is the whole point, since it's the grouping key
    /// the comparison relies on.
    ///
    /// Machines routinely have more than one GPU (integrated plus discrete), which is exactly why
    /// this can't just take the first: "first GPU" alphabetically resolved to the untouched
    /// integrated one, so every trace reported IRQ priority Undefined regardless of what was set.
    ///
    /// USB and audio controllers are captured alongside the GPUs, not just GPUs: those are the two
    /// categories InterruptDeviceAdvisor actually tells the user to raise to High, and while only GPUs
    /// were recorded here, changing them left the label identical — so before and after traces landed
    /// in the same config group and the comparison reported "all with the same config", unable to say
    /// whether the recommended change had helped.
    /// </summary>
    private IReadOnlyList<TunedDeviceConfig> CaptureTunableConfigs()
    {
        try
        {
            return _deviceEnumerator.EnumerateInterruptCapableDevices()
                .Where(d => TrackedConfigCategories.Contains(d.CategoryLabel))
                .OrderBy(d => d.CategoryLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(d => new TunedDeviceConfig(d.FriendlyName, d.Priority, d.IsMsiEnabled, d.CategoryLabel))
                .ToList();
        }
        catch
        {
            // Config capture is a convenience; never let it break a completed trace.
            return [];
        }
    }

    [RelayCommand]
    private void SaveTrace()
    {
        if (LastResult is not { } result || IsSaved)
        {
            return;
        }

        ComparisonMessage = DpcIsrComparisonGenerator.Generate(result, _historyStore.Results.ToList());
        _historyStore.Add(result);
        IsSaved = true;
        StatusMessage = $"Saved ({result.ConfigLabel}).";
    }

    [RelayCommand]
    private void ClearTraces()
    {
        _historyStore.Clear();
        ComparisonMessage = null;
    }

    private void RefreshCoreLoads()
    {
        var total = _coreEventCounts.Values.Sum();
        if (total == 0)
        {
            return;
        }

        var busiest = _coreEventCounts.Values.Max();

        CoreLoads.Clear();
        foreach (var (core, count) in _coreEventCounts.OrderBy(kv => kv.Key))
        {
            CoreLoads.Add(new CoreInterruptLoadViewModel
            {
                CoreIndex = core,
                EventCount = count,
                SharePercent = 100.0 * count / total,
                Fraction = busiest > 0 ? (double)count / busiest : 0,
            });
        }
    }

    private void RefreshTopDrivers()
    {
        TopDrivers.Clear();
        foreach (var entry in _driverStats.OrderByDescending(kv => kv.Value.MaxMicroseconds).Take(5))
        {
            TopDrivers.Add(new DriverStatViewModel
            {
                DriverName = entry.Key,
                MaxDurationMicroseconds = entry.Value.MaxMicroseconds,
                SampleCount = entry.Value.Count,
            });
        }
    }

    public void Dispose()
    {
        _drainTimer.Stop();
        _session.Dispose();
    }
}
