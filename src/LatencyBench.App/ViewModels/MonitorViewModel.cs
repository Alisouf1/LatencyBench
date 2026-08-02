using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Monitoring;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.App.ViewModels;

/// <summary>
/// Live view over <see cref="LatencyMonitor"/>.
/// <para>
/// The monitor's own events fire off the background sampling thread, never on the UI thread. This
/// view-model is the one place that marshals them across, via a dispatcher timer that drains a
/// queue rather than dispatching per-sample — the same batching the DPC/ISR tab uses, for the same
/// reason: dispatching individually would put a cross-thread call on the UI thread every couple of
/// seconds forever, for no benefit over draining a short queue on a timer.
/// </para>
/// </summary>
public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly LatencyMonitor _monitor;
    private readonly System.Collections.Concurrent.ConcurrentQueue<MonitoringSample> _pendingSamples = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<MonitoringWarning> _pendingWarnings = new();
    private readonly DispatcherTimer _drainTimer;

    /// <summary>Warning log entries are capped so a monitor left running overnight does not grow the
    /// UI's collection without bound.</summary>
    private const int MaxWarningLogEntries = 200;

    public ObservableCollection<double> DpcHistory { get; } = new();

    public ObservableCollection<double> InterruptHistory { get; } = new();

    public ObservableCollection<WarningLogEntryViewModel> WarningLog { get; } = new();

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private double _currentInterruptTimePercent;

    [ObservableProperty]
    private double _currentDpcTimePercent;

    [ObservableProperty]
    private double _currentProcessorTimePercent;

    [ObservableProperty]
    private double _currentAvailableMemoryPercent = 100.0;

    [ObservableProperty]
    private string? _status;

    public MonitorViewModel(LatencyMonitor monitor)
    {
        _monitor = monitor;
        _monitor.SampleReceived += sample => _pendingSamples.Enqueue(sample);
        _monitor.WarningRaised += warning => _pendingWarnings.Enqueue(warning);

        _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _drainTimer.Tick += (_, _) => Drain();
        _drainTimer.Start();
    }

    private void Drain()
    {
        MonitoringSample? latest = null;
        while (_pendingSamples.TryDequeue(out MonitoringSample? sample))
        {
            latest = sample;
            AppendBounded(DpcHistory, sample.DpcTimePercent);
            AppendBounded(InterruptHistory, sample.InterruptTimePercent);
        }

        if (latest is not null)
        {
            CurrentInterruptTimePercent = latest.InterruptTimePercent;
            CurrentDpcTimePercent = latest.DpcTimePercent;
            CurrentProcessorTimePercent = latest.ProcessorTimePercent;
            CurrentAvailableMemoryPercent = latest.AvailableMemoryPercent;
        }

        while (_pendingWarnings.TryDequeue(out MonitoringWarning? warning))
        {
            WarningLog.Insert(0, new WarningLogEntryViewModel(warning));
            while (WarningLog.Count > MaxWarningLogEntries)
            {
                WarningLog.RemoveAt(WarningLog.Count - 1);
            }
        }
    }

    /// <summary>Keeps a fixed-width rolling window for the sparkline, matching the pattern the mouse
    /// test chart already uses rather than letting the chart series grow forever.</summary>
    private static void AppendBounded(ObservableCollection<double> series, double value, int max = 120)
    {
        series.Add(value);
        while (series.Count > max)
        {
            series.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            _monitor.Stop();
            IsMonitoring = false;
            Status = "Monitoring stopped.";
        }
        else
        {
            _monitor.Start();
            IsMonitoring = true;
            Status = "Monitoring started — sampling every 2 seconds.";
        }
    }

    [RelayCommand]
    private void ClearLog() => WarningLog.Clear();

    /// <summary>Called from the window's closing handler — the monitor must not keep sampling after
    /// the app that would show its warnings has gone, and its performance counter handles must be
    /// released rather than left for finalization.</summary>
    public void Shutdown()
    {
        _drainTimer.Stop();
        _monitor.Dispose();
    }
}

public sealed class WarningLogEntryViewModel
{
    public WarningLogEntryViewModel(MonitoringWarning warning)
    {
        Timestamp = warning.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        Message = warning.Message;
        Severity = warning.Severity;
        IsRecovery = warning.IsRecovery;
    }

    public string Timestamp { get; }

    public string Message { get; }

    public WarningSeverity Severity { get; }

    public bool IsRecovery { get; }
}
