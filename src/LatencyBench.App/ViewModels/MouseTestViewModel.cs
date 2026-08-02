using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using LatencyBench.Core.HidTesting;
using LatencyBench.Core.Models;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.MouseTesting.Models;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class MouseTestViewModel : ObservableObject
{
    /// <summary>Fixed plot size the chart is rendered at — matches the Canvas size in MouseTestView.xaml.</summary>
    private const double ChartWidth = 640;
    private const double ChartHeight = 260;

    /// <summary>Generous upfront List capacity so a fast/long capture at a high polling rate doesn't pay for list resizes — the run's actual length is unknown ahead of time since capture is manually started/stopped.</summary>
    private const int SampleCapacityHint = 20_000;

    private readonly HidDeviceEnumerator _deviceEnumerator = new();
    private readonly UsbTreeEnumerator _treeEnumerator;
    private readonly MouseTestHistoryStore _historyStore;
    private readonly RawMouseSampleCapture _capture = new();
    private readonly DispatcherTimer _liveTimer;
    private IntPtr _windowHandle;
    private DateTime _captureStartedAt;
    private bool _loadedOnce;

    /// <summary>The device's label, snapshotted when the capture actually starts rather than read
    /// from <see cref="SelectedDevice"/> again in <see cref="StopCapture"/> — if the device is
    /// unplugged (or the picker's selection otherwise changes) between Start and Stop, SelectedDevice
    /// can become null while a capture is still in progress, and the result should still be
    /// attributed to whichever device was actually captured.</summary>
    private string? _capturingDeviceLabel;

    public ObservableCollection<MouseDeviceOption> Devices { get; } = [];

    public ObservableCollection<MouseSessionRowViewModel> SavedResults { get; } = [];

    public ObservableCollection<AxisTick> XTicks { get; } = [];

    public ObservableCollection<AxisTick> YTicks { get; } = [];

    /// <summary>
    /// Bound to the Polyline through a value converter rather than as an ItemsSource, so — unlike
    /// XTicks/YTicks above — mutating a fixed collection in place (Clear()+Add()) does not reliably
    /// refresh it: at small sample counts the rebind happened to catch the final state, but at real
    /// capture sizes (tens of thousands of samples) the Polyline was found to render the empty
    /// Clear() state and never pick up the subsequent Add()s. Reassigning the property outright (a
    /// real PropertyChanged notification) is what WPF's binding engine actually guarantees.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _chartPoints = [];

    /// <summary>The second overlaid series, populated only while comparing two saved runs. Same reassignment reasoning as ChartPoints.</summary>
    [ObservableProperty]
    private IReadOnlyList<ChartPoint> _comparePoints = [];

    [ObservableProperty]
    private MouseDeviceOption? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChartData))]
    private MouseChartMode _selectedChartMode = MouseChartMode.XCountsVsTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasElapsedTime))]
    private bool _isTesting;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private int _liveSampleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedTimeLabel))]
    private double _elapsedSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChartData))]
    [NotifyPropertyChangedFor(nameof(HasElapsedTime))]
    private MouseTestResult? _lastResult;

    [ObservableProperty]
    private bool _isSaved;

    [ObservableProperty]
    private double? _calibrationDistanceInches;

    [ObservableProperty]
    private double? _measuredCpi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChartData))]
    private bool _isComparing;

    [ObservableProperty]
    private string? _compareSummary;

    public bool HasChartData => IsComparing || LastResult is not null;

    /// <summary>True while a capture is running AND after it finishes, so the timer doesn't just vanish the instant Stop is clicked — it freezes at the final elapsed time instead, next to the result.</summary>
    public bool HasElapsedTime => IsTesting || LastResult is not null;

    /// <summary>mm:ss — a real timer readout instead of a raw decimal-seconds count, since a capture can run well past a minute.</summary>
    public string ElapsedTimeLabel => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"mm\:ss");

    public MouseTestViewModel(MouseTestHistoryStore historyStore, UsbTreeEnumerator treeEnumerator)
    {
        _historyStore = historyStore;
        _treeEnumerator = treeEnumerator;
        _historyStore.Results.CollectionChanged += (_, _) => RefreshFromHistory();
        RefreshFromHistory();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _liveTimer.Tick += (_, _) =>
        {
            LiveSampleCount = _capture.Samples.Count;
            ElapsedSeconds = (DateTime.Now - _captureStartedAt).TotalSeconds;
        };
    }

    public void SetWindowHandle(IntPtr handle) => _windowHandle = handle;

    /// <summary>Forward WM_INPUT here from the hosting window's message hook.</summary>
    public void OnRawInputMessage(int msg, IntPtr lParam) => _capture.OnRawInputMessage(msg, lParam);

    public void LoadIfNeeded()
    {
        if (!_loadedOnce)
        {
            RefreshDevices();
            _loadedOnce = true;
        }
    }

    /// <summary>Matches the "&amp;MI_nn" USB interface-number segment of a HID instance ID.</summary>
    private static readonly Regex InterfaceNumberPattern = new(@"&MI_(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// A mouse's own HID collection almost always reports itself as a generic Windows string ("HID-
    /// compliant mouse") — every mouse looks the same in the picker, which is useless once more than
    /// one is plugged in. The real product name (e.g. "MCHOSE M7 Ultra") lives on a sibling interface
    /// under the same physical USB device, so this walks the USB tree to find it — the exact same
    /// resolution Port Test already uses for its own device list (see
    /// UsbDeviceNodeExtensions.FindPhysicalDeviceAncestor / GetBestDisplayName).
    ///
    /// A keyboard's volume knob or scroll wheel is sometimes reported under the same Generic Desktop
    /// "Mouse" usage a real pointing device uses, and Windows' own per-collection Class genuinely
    /// calls that specific collection "Mouse" too (confirmed on HE68 Lite: its knob collection has
    /// DeviceClass="Mouse", exactly like a real mouse's primary collection) — so neither the usage
    /// numbers nor the per-collection Class can tell the two apart, and reading the device's HID
    /// capabilities directly (button/axis counts) isn't possible either: Windows denies raw access to
    /// any collection its own mouse/keyboard class driver has already claimed (ACCESS_DENIED),
    /// confirmed against this exact hardware.
    ///
    /// What does distinguish them: which USB interface number is the device's primary one. A composite
    /// device's interface 0 is conventionally its main declared function. HE68 Lite's keyboard
    /// collection sits on interface 0, and its mouse-shaped knob collection is a sub-collection of a
    /// *different*, higher-numbered interface; MCHOSE M7 Ultra is the reverse — its mouse collection
    /// IS interface 0, and its keyboard collection (macro buttons) lives on a different interface. So a
    /// physical device is excluded here only when interface 0 itself is keyboard-classed — a mouse that
    /// also happens to expose a keyboard collection elsewhere (MCHOSE's macro buttons) is unaffected.
    ///
    /// Grouped by the physical USB tree node (FindPhysicalDeviceAncestor), not by VID/PID text: cheap
    /// peripherals commonly reuse a generic silicon-vendor VID/PID unmodified, so two genuinely
    /// different products (an unrelated keyboard and mouse) can share an identical VID/PID string —
    /// grouping by that string could cross-contaminate the exclusion between them. The physical node's
    /// own instance ID encodes its actual hub/port position, which is unique per connected device.
    /// </summary>
    [RelayCommand]
    private void RefreshDevices()
    {
        var previousPath = SelectedDevice?.Device.DevicePath;
        Devices.Clear();

        var hostControllers = _treeEnumerator.EnumerateHostControllers();
        var allDevices = _deviceEnumerator.EnumerateDevices();
        var physicalNodeByInstanceId = allDevices.ToDictionary(
            d => d.InstanceId,
            d => hostControllers.FindPhysicalDeviceAncestor(d.InstanceId).PhysicalNode,
            StringComparer.OrdinalIgnoreCase);

        var keyboardIsPrimaryInterface = allDevices
            .Select(d => (Device: d, PhysicalId: physicalNodeByInstanceId[d.InstanceId]?.InstanceId, Interface: ParseInterfaceNumber(d.InstanceId)))
            .Where(x => x.PhysicalId is not null && x.Interface is not null)
            .GroupBy(x => x.PhysicalId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.OrderBy(x => x.Interface).First().Device.IsKeyboard)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in allDevices.Where(d => d.IsMouse))
        {
            var physicalNode = physicalNodeByInstanceId[device.InstanceId];
            if (physicalNode is not null && keyboardIsPrimaryInterface.Contains(physicalNode.InstanceId))
            {
                continue;
            }

            var displayName = physicalNode?.GetBestDisplayName() ?? device.FriendlyName;
            Devices.Add(new MouseDeviceOption(device, displayName));
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.Device.DevicePath == previousPath) ?? Devices.FirstOrDefault();
    }

    private static int? ParseInterfaceNumber(string instanceId)
    {
        var match = InterfaceNumberPattern.Match(instanceId);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    [RelayCommand]
    private void StartCapture()
    {
        if (SelectedDevice is null)
        {
            StatusMessage = "Select a mouse first.";
            return;
        }

        if (_windowHandle == IntPtr.Zero)
        {
            StatusMessage = "Window isn't ready yet — try again in a moment.";
            return;
        }

        var (usagePage, usage) = SelectedDevice.Device.EffectiveUsage;
        if (!_capture.Start(_windowHandle, SelectedDevice.Device.DevicePath, usagePage, usage, SampleCapacityHint))
        {
            StatusMessage = "Could not start capture for this device.";
            return;
        }

        IsTesting = true;
        IsComparing = false;
        LastResult = null;
        IsSaved = false;
        MeasuredCpi = null;
        LiveSampleCount = 0;
        ElapsedSeconds = 0;
        ChartPoints = [];
        ComparePoints = [];
        XTicks.Clear();
        YTicks.Clear();
        _capturingDeviceLabel = SelectedDevice.DisplayLabel;
        _captureStartedAt = DateTime.Now;
        _liveTimer.Start();
        StatusMessage = "Move the mouse now — click \"Stop\" when done.";
    }

    [RelayCommand]
    private void StopCapture()
    {
        _liveTimer.Stop();
        _capture.Stop();
        IsTesting = false;

        var samples = _capture.Samples;
        var analysis = MouseMotionAnalyzer.Analyze(samples, RawMouseSampleCapture.TicksPerMillisecond);
        if (analysis is null)
        {
            StatusMessage = "Not enough movement was captured — try again and keep moving the mouse.";
            return;
        }

        LastResult = new MouseTestResult
        {
            DeviceFriendlyName = _capturingDeviceLabel ?? "Unknown device",
            Samples = samples.ToList(),
            SampleCount = analysis.Value.SampleCount,
            DurationMs = analysis.Value.DurationMs,
            EffectiveReportRateHz = analysis.Value.EffectiveReportRateHz,
            TotalPathCounts = analysis.Value.TotalPathCounts,
            NetDisplacementCounts = analysis.Value.NetDisplacementCounts,
            PeakSpeedCountsPerMs = analysis.Value.PeakSpeedCountsPerMs,
            SpikeSampleIndices = analysis.Value.SpikeSampleIndices,
            AngleSnapScore = analysis.Value.AngleSnapScore,
            SpeedGainBands = analysis.Value.SpeedGainBands,
        };

        RebuildChart();
        StatusMessage = $"Captured {analysis.Value.SampleCount} samples over {analysis.Value.DurationMs / 1000:0.0}s. " +
            "Enter a known distance below to measure CPI, or save this run.";
    }

    [RelayCommand]
    private void MeasureCpi()
    {
        if (LastResult is null || CalibrationDistanceInches is not > 0)
        {
            return;
        }

        MeasuredCpi = LastResult.TotalPathCounts / CalibrationDistanceInches.Value;
        StatusMessage = $"Measured CPI: {MeasuredCpi:0} (total path {LastResult.TotalPathCounts:0} counts over {CalibrationDistanceInches:0.##} in — only meaningful if that whole path was one straight stroke).";
    }

    [RelayCommand]
    private void SaveResult()
    {
        if (LastResult is null || IsSaved)
        {
            return;
        }

        _historyStore.Add(MouseTestSession.FromResult(LastResult, MeasuredCpi));
        IsSaved = true;
        StatusMessage = "Saved — this run now appears below and can be picked for comparison.";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (LastResult is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"mouse-test-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            InitialDirectory = AppDataDirectory(),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        using var writer = new StreamWriter(dialog.FileName);
        MouseCsvExporter.Write(writer, LastResult.Samples, RawMouseSampleCapture.TicksPerMillisecond);
        StatusMessage = $"Exported {LastResult.Samples.Count} samples to {dialog.FileName}.";
    }

    [RelayCommand]
    private void CompareSelected()
    {
        var selected = SavedResults.Where(r => r.IsSelectedForCompare).Select(r => r.Session).ToList();
        if (selected.Count != 2)
        {
            StatusMessage = "Select exactly two saved runs (checkbox) to compare.";
            return;
        }

        var overlay = MouseChartBuilder.BuildOverlay(
            selected[0].Trace, selected[1].Trace, SelectedChartMode, ChartWidth, ChartHeight, RawMouseSampleCapture.TicksPerMillisecond);
        if (overlay is null)
        {
            StatusMessage = "Not enough data in the selected runs to compare.";
            return;
        }

        var (chartA, chartB) = overlay.Value;
        ChartPoints = chartA.Points;
        ComparePoints = chartB.Points;
        ReplaceTicks(XTicks, chartA.XTicks);
        ReplaceTicks(YTicks, chartA.YTicks);

        IsComparing = true;
        CompareSummary =
            $"A ({selected[0].SavedAt:MM/dd HH:mm}) — {selected[0].DeviceFriendlyName}: peak {selected[0].PeakSpeedCountsPerMs:0.0} counts/ms, path {selected[0].TotalPathCounts:0} counts, angle-snap {selected[0].AngleSnapScore:0.00}\n" +
            $"B ({selected[1].SavedAt:MM/dd HH:mm}) — {selected[1].DeviceFriendlyName}: peak {selected[1].PeakSpeedCountsPerMs:0.0} counts/ms, path {selected[1].TotalPathCounts:0} counts, angle-snap {selected[1].AngleSnapScore:0.00}";
        StatusMessage = "Comparing two saved runs — A is solid, B is dashed.";
    }

    [RelayCommand]
    private void ClearCompare()
    {
        IsComparing = false;
        CompareSummary = null;
        ComparePoints = [];
        foreach (var row in SavedResults)
        {
            row.IsSelectedForCompare = false;
        }

        RebuildChart();
    }

    partial void OnSelectedChartModeChanged(MouseChartMode value)
    {
        if (IsComparing)
        {
            CompareSelectedCommand.Execute(null);
        }
        else
        {
            RebuildChart();
        }
    }

    private void RebuildChart()
    {
        if (LastResult is null)
        {
            ChartPoints = [];
            XTicks.Clear();
            YTicks.Clear();
            return;
        }

        var chart = MouseChartBuilder.Build(LastResult.Samples, SelectedChartMode, ChartWidth, ChartHeight, RawMouseSampleCapture.TicksPerMillisecond);
        if (chart is null)
        {
            return;
        }

        ChartPoints = chart.Value.Points;
        ReplaceTicks(XTicks, chart.Value.XTicks);
        ReplaceTicks(YTicks, chart.Value.YTicks);
    }

    private void RefreshFromHistory()
    {
        SavedResults.Clear();
        foreach (var session in _historyStore.Results.OrderByDescending(s => s.SavedAt))
        {
            SavedResults.Add(new MouseSessionRowViewModel(session));
        }
    }

    private static void ReplaceTicks(ObservableCollection<AxisTick> target, IReadOnlyList<AxisTick> ticks)
    {
        target.Clear();
        foreach (var tick in ticks)
        {
            target.Add(tick);
        }
    }

    private static string AppDataDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatencyBench");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
