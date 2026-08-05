using System.IO;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.HidTesting;
using LatencyBench.Core.HidTesting.Models;
using LatencyBench.Core.Models;
using LatencyBench.Core.Perf;
using LatencyBench.Core.PortTesting;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class PortTestViewModel : ObservableObject, IDisposable
{
    /// <summary>Public so "Run full diagnostic" can match the DPC/ISR trace it runs alongside to this same length — mismatched durations meant the trace kept running silently after the port test's own progress UI had already shown "complete", reading as if nothing but the port test had happened.</summary>
    public const int TestDurationSeconds = 6;
    private const int ProgressTickMilliseconds = 200;
    private const int LiveGraphSampleCount = 40;

    /// <summary>
    /// Only DPC/ISR events at least this long are considered capable of delaying a HID report. A
    /// 4000 Hz mouse polls every 250 us, so events far below this can't push a report past the
    /// 1.5x-median late threshold, and including them would swamp the correlation with noise.
    /// </summary>
    private const double SpikeThresholdMicroseconds = 50;

    private readonly HidDeviceEnumerator _deviceEnumerator = new();
    private readonly HidPortTestEngine _engine = new();
    private readonly CpuLoadGenerator _loadGenerator = new();
    private readonly PortTestHistoryStore _historyStore;
    private readonly UsbTreeEnumerator _treeEnumerator;
    private IntPtr _windowHandle;
    private bool _loadedOnce;
    private bool _lastRunUnderLoad;
    private string? _lastRunHostControllerInstanceId;

    public ObservableCollection<TestableDeviceOption> Devices { get; } = [];

    public ObservableCollection<double> LiveIntervals { get; } = [];

    [ObservableProperty]
    private TestableDeviceOption? _selectedDevice;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private HidTestResult? _lastResult;

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private int _liveSampleCount;

    [ObservableProperty]
    private double? _liveJitterMs;

    [ObservableProperty]
    private double? _liveMedianReportIntervalMs;

    [ObservableProperty]
    private double? _livePollingRateHz;

    [ObservableProperty]
    private string _advisorMessage = AdvisorMessageGenerator.Generate([]);

    [ObservableProperty]
    private bool _isSaved;

    /// <summary>How this run compares to the best previous run on the same physical port — the proof that a change actually helped.</summary>
    [ObservableProperty]
    private PortComparison? _comparison;

    /// <summary>Load the CPU during the test, so the numbers reflect gaming conditions rather than an idle desktop.</summary>
    [ObservableProperty]
    private bool _testUnderLoad;

    /// <summary>Warns when the tested device shares its controller with bandwidth-heavy devices.</summary>
    [ObservableProperty]
    private string? _contentionMessage;

    /// <summary>Trace DPC/ISR during the test to find which driver is actually delaying reports.</summary>
    [ObservableProperty]
    private bool _diagnoseLateReports;

    /// <summary>The correlation verdict — which driver (if any) is causing late reports.</summary>
    [ObservableProperty]
    private string? _diagnosisMessage;

    public string LoadDescription => $"Test under CPU load ({_loadGenerator.ThreadCount} of {Environment.ProcessorCount} cores busy)";

    /// <summary>Report-interval distribution for the last completed test.</summary>
    public ObservableCollection<HistogramBucket> IntervalHistogram { get; } = [];

    /// <summary>Saved results so far, combined per physical port (same controller + port) with the best run headlining each — shared with the Dashboard's underlying store.</summary>
    public ObservableCollection<PortHistoryGroupViewModel> SavedResults { get; } = [];

    public PortTestViewModel(PortTestHistoryStore historyStore, UsbTreeEnumerator treeEnumerator)
    {
        _historyStore = historyStore;
        _treeEnumerator = treeEnumerator;
        _historyStore.Results.CollectionChanged += (_, _) => RefreshFromHistory();
        RefreshFromHistory();
    }

    public void SetWindowHandle(IntPtr handle) => _windowHandle = handle;

    /// <summary>Forward WM_INPUT here from the hosting window's message hook.</summary>
    public void OnRawInputMessage(int msg, IntPtr lParam) => _engine.OnRawInputMessage(msg, lParam);

    public void LoadIfNeeded()
    {
        if (!_loadedOnce)
        {
            RefreshDevices();
            _loadedOnce = true;
        }
    }

    /// <summary>
    /// Groups raw HID collections by the physical device they belong to, so a combo receiver
    /// (mouse + keyboard + consumer buttons, three-plus separate Windows HID collections) shows as
    /// one recognizable entry instead of a wall of near-duplicate technical sub-interfaces. All
    /// collections in a group share the same physical USB connection, so testing any one of them
    /// tells you about that port — the group picks mouse, then keyboard, as the one actually used.
    ///
    /// Restricted to mouse and keyboard collections specifically — a HID device can expose other
    /// collections too (a USB microphone's mute button/volume knob, a vendor-defined control surface),
    /// and those aren't meaningful "ports" to offer for jitter testing here.
    /// </summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        var previousId = SelectedDevice?.Device.InstanceId;
        Devices.Clear();

        var hostControllers = _treeEnumerator.EnumerateHostControllers();
        var controllerNumbers = hostControllers
            .Select((hc, i) => (hc.InstanceId, Number: i + 1))
            .ToDictionary(x => x.InstanceId, x => x.Number, StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, List<HidDeviceInfo>>();
        var groupPhysicalNode = new Dictionary<string, UsbDeviceNode>();
        var groupHostController = new Dictionary<string, UsbDeviceNode>();

        foreach (var device in _deviceEnumerator.EnumerateDevices().Where(d => d.IsMouse || d.IsKeyboard))
        {
            var (physicalNode, hostController) = hostControllers.FindPhysicalDeviceAncestor(device.InstanceId);
            var groupKey = physicalNode?.InstanceId ?? device.InstanceId;

            if (!groups.TryGetValue(groupKey, out var members))
            {
                members = [];
                groups[groupKey] = members;
                if (physicalNode is not null)
                {
                    groupPhysicalNode[groupKey] = physicalNode;
                }

                if (hostController is not null)
                {
                    groupHostController[groupKey] = hostController;
                }
            }

            members.Add(device);
        }

        foreach (var (key, members) in groups)
        {
            var displayName = groupPhysicalNode.TryGetValue(key, out var physicalNode)
                ? physicalNode.GetBestDisplayName()
                : members[0].FriendlyName;

            // Composite peripherals expose several collections: a keyboard (HE68) also has a mouse
            // collection for media keys, a mouse (MCHOSE) also has a keyboard collection for extra
            // buttons. Rather than guess which one to test from the product name — unreliable, since
            // neither name reveals its type — carry both the mouse and keyboard collection and let
            // the engine capture both, so whichever the user actually drives is what gets measured.
            var mouseCollection = members.FirstOrDefault(d => d.IsMouse);
            var keyboardCollection = members.FirstOrDefault(d => d.IsKeyboard);
            var representative = mouseCollection ?? keyboardCollection ?? members[0];

            string? location = groupHostController.TryGetValue(key, out var hostController) && physicalNode?.PortNumber is int port
                ? $"Controller {controllerNumbers.GetValueOrDefault(hostController.InstanceId, 0)}, port {port}"
                : null;

            var label = location is null ? displayName : $"{displayName} — {location}";
            Devices.Add(new TestableDeviceOption
            {
                Device = representative,
                MouseCollection = mouseCollection,
                KeyboardCollection = keyboardCollection,
                DisplayLabel = label,
                PortLocation = location ?? displayName,
                HostControllerInstanceId = hostController?.InstanceId,
            });
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Device.InstanceId == previousId) ?? Devices.FirstOrDefault();

        ContentionMessage = ControllerContentionAdvisor.Generate(
            hostControllers, hostControllers.Select(hc => controllerNumbers.GetValueOrDefault(hc.InstanceId, 0)).ToList());
    }

    [RelayCommand]
    private async Task StartTestAsync()
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Select a device first.";
            return;
        }

        var device = SelectedDevice.Device;
        _lastRunHostControllerInstanceId = SelectedDevice.HostControllerInstanceId;

        if (_windowHandle == IntPtr.Zero)
        {
            StatusMessage = "Window isn't ready yet — try again in a moment.";
            return;
        }

        if (!_engine.StartCapture(_windowHandle, device, SelectedDevice.MouseCollection, SelectedDevice.KeyboardCollection))
        {
            StatusMessage = "Could not start capture for this device.";
            return;
        }

        IsTesting = true;
        LastResult = null;
        IsSaved = false;
        Comparison = null;
        // Must be cleared too: otherwise a diagnosis from an earlier run stays on screen next to a
        // run that wasn't diagnosed at all, reading as though it described this result.
        DiagnosisMessage = null;
        IntervalHistogram.Clear();
        LiveSampleCount = 0;
        LiveJitterMs = null;
        LiveMedianReportIntervalMs = null;
        LivePollingRateHz = null;
        LiveIntervals.Clear();
        var hasMouse = SelectedDevice.MouseCollection is not null;
        var hasKeyboard = SelectedDevice.KeyboardCollection is not null;
        StatusMessage = hasMouse && hasKeyboard ? "Use the device continuously now — type on it or move it…"
            : hasKeyboard ? "Press keys continuously now…"
            : hasMouse ? "Move the mouse continuously now…"
            : "Interact with the device now (press its buttons/controls)…";

        var portLabel = SelectedDevice.DisplayLabel;
        var portLocation = SelectedDevice.PortLocation;
        var underLoad = TestUnderLoad;
        HidTestResult? result;
        DpcIsrTraceSession? traceSession = null;
        var traceSamples = new ConcurrentQueue<DpcIsrSample>();

        try
        {
            if (underLoad)
            {
                _loadGenerator.Start();
            }

            if (DiagnoseLateReports)
            {
                try
                {
                    traceSession = new DpcIsrTraceSession();
                    traceSession.SampleReceived += traceSamples.Enqueue;
                    traceSession.Start();
                }
                catch (Exception ex)
                {
                    // Only one NT Kernel Logger exists system-wide, so this fails if the DPC/ISR tab
                    // (or LatencyMon) is already tracing. Run the test anyway, just without diagnosis.
                    traceSession?.Dispose();
                    traceSession = null;
                    DiagnosisMessage = $"Couldn't trace drivers during this test: {ex.Message}";
                }
            }

            var totalTicks = TestDurationSeconds * 1000 / ProgressTickMilliseconds;
            for (var tick = 0; tick < totalTicks; tick++)
            {
                await Task.Delay(ProgressTickMilliseconds);
                SecondsRemaining = TestDurationSeconds - (tick * ProgressTickMilliseconds / 1000);
                LiveSampleCount = _engine.CurrentSampleCount;

                LiveIntervals.Clear();
                foreach (var interval in _engine.GetRecentIntervalsMs(LiveGraphSampleCount))
                {
                    LiveIntervals.Add(interval);
                }

                var liveAnalysis = _engine.GetCurrentAnalysis();
                LiveJitterMs = liveAnalysis?.JitterMs;
                LiveMedianReportIntervalMs = liveAnalysis?.MedianReportIntervalMs;
                LivePollingRateHz = liveAnalysis?.PollingRateHz;
            }

            result = _engine.StopCaptureAndAnalyze(device.FriendlyName, portLabel, portLocation);
        }
        finally
        {
            // Guarantees the OS-level capture (a global keyboard hook, or raw-input registration)
            // is released even if the loop above is interrupted — a no-op on the normal path since
            // StopCaptureAndAnalyze already stopped it. Same for the load threads: leaving those
            // spinning would peg the machine.
            _engine.EnsureStopped();
            _loadGenerator.Stop();
            traceSession?.Dispose();
            IsTesting = false;
        }

        if (result is null)
        {
            StatusMessage = "Not enough input was captured — try moving/pressing the device more during the test.";
            return;
        }

        LastResult = result;

        _lastRunUnderLoad = underLoad;

        // Compared against saved history *before* this run is added, so the baseline is what you had
        // before whatever change you're testing. Restricted to the same report cadence (mouse-like vs
        // keyboard-like) as this run, so a keyboard test is never measured against a mouse's baseline
        // on a shared physical port, or vice versa.
        var isHighFrequency = result.PollingRateHz >= PortHistoryGrouping.HighFrequencyThresholdHz;
        Comparison = PortComparisonGenerator.Compare(result.JitterMs, result.PortLocation, _historyStore.Results.ToList(), underLoad, isHighFrequency);

        IntervalHistogram.Clear();
        foreach (var bucket in IntervalHistogramBuilder.Build(result.ActiveIntervalsMs, result.MedianReportIntervalMs))
        {
            IntervalHistogram.Add(bucket);
        }

        if (!traceSamples.IsEmpty)
        {
            RunDiagnosis(result, traceSamples);
        }

        StatusMessage = $"Test complete — {result.SampleCount} samples from {portLabel}. Click \"Save result\" to add it to your results and update the advisor.";
    }

    [RelayCommand]
    private void SaveResult()
    {
        if (LastResult is not { } result || IsSaved)
        {
            return;
        }

        var rank = PortScoringService.Score(result.JitterMs, result.PollingRateHz);
        _historyStore.Add(new PortRankResult
        {
            PortLabel = result.PortLabel,
            PortLocation = result.PortLocation,
            Rank = rank,
            AverageJitterMs = result.JitterMs,
            AverageReportIntervalMs = result.MedianReportIntervalMs,
            PollingRateHz = (int)Math.Round(result.PollingRateHz),
            UnderLoad = _lastRunUnderLoad,
            HostControllerInstanceId = _lastRunHostControllerInstanceId,
        });

        IsSaved = true;
        StatusMessage = $"Saved — {result.SampleCount} samples from {result.PortLabel}. Results and the advisor's pick update below and on the Dashboard.";
    }

    /// <summary>
    /// Lines the captured report windows up against the DPC/ISR events traced during the same run to
    /// find which driver is actually delaying reports. Only events long enough to plausibly delay a
    /// report are considered — at tens of thousands of events per second, correlating against all of
    /// them would match everything and prove nothing.
    /// </summary>
    private void RunDiagnosis(HidTestResult result, ConcurrentQueue<DpcIsrSample> traceSamples)
    {
        var windows = _engine.GetReportWindows(result.MedianReportIntervalMs);
        var spikes = traceSamples
            .Where(s => s.DurationMicroseconds >= SpikeThresholdMicroseconds)
            .Select(s => new SpikeEvent(s.Timestamp, s.DurationMicroseconds, s.DriverName))
            .ToList();

        var correlation = LateReportCorrelator.Correlate(windows, spikes);
        DiagnosisMessage = correlation.Message +
            "\n\nNote: kernel tracing adds overhead of its own, so treat this run's jitter as indicative — " +
            "re-test without diagnosis for a clean measurement.";
    }

    /// <summary>Rebuilds the combined per-port results list and the advisor message from the shared history store — called on every Add/Clear, from either this page or the Dashboard.</summary>
    private void RefreshFromHistory()
    {
        var groups = PortHistoryGrouping.GroupByPort(_historyStore.Results);

        SavedResults.Clear();
        foreach (var group in groups)
        {
            SavedResults.Add(new PortHistoryGroupViewModel(group));
        }

        AdvisorMessage = AdvisorMessageGenerator.Generate(groups.Select(g => g.Best).ToList());
    }

    /// <summary>
    /// Releases the CPU load generator, which owns a cancellation source and the worker threads it
    /// spins up for load-testing. Stop() is called at the end of each test, but Dispose never was, so
    /// the generator's own resources were left to process exit rather than released deterministically.
    /// Called from MainViewModel.Shutdown alongside the other OS-resource owners.
    /// </summary>
    public void Dispose() => _loadGenerator.Dispose();
}
