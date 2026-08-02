using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Perf;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class AffinityViewModel : ObservableObject
{
    /// <summary>Non-USB categories also shown here, in display order. Category must match <see cref="InterruptDeviceEnumerator"/>'s CategoryLabel exactly; RestartCategory must match the keys DeviceRestartService recognizes (the two aren't always spelled the same way).</summary>
    private static readonly (string Category, string LabelPrefix, string RestartCategory)[] ExtraDeviceCategories =
    [
        ("GPU", "GPU", "GPU"),
        ("Storage controller", "Storage Controller", "Storage controller"),
        ("Audio controller", "Audio Device", "Audio controller"),
    ];

    /// <summary>Below this many saved traces the average is still thin enough that the status message says so rather than implying settled evidence.</summary>
    private const int RecommendedTraceCount = 3;

    private readonly InterruptAffinityService _service;
    private readonly DeviceRestartService _restartService;
    private readonly UsbTreeEnumerator _treeEnumerator;
    private readonly InterruptDeviceEnumerator _deviceEnumerator;
    private readonly CpuUsageMonitor _cpuUsageMonitor;
    private readonly DispatcherTimer _usageTimer;
    private bool _loadedOnce;
    private IReadOnlyList<CoreLoad>? _interruptLoads;
    private int _interruptTraceCount;
    private double _interruptTotalSeconds;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _optimizeStatusMessage;

    public ObservableCollection<HostControllerViewModel> Controllers { get; } = [];

    /// <param name="cpuUsageMonitor">Defaults to a real <see cref="CpuUsageMonitor"/> — overridable so
    /// a test can substitute one that simulates a failed read without needing to actually break the
    /// underlying syscall.</param>
    public AffinityViewModel(
        InterruptAffinityService service,
        DeviceRestartService restartService,
        UsbTreeEnumerator treeEnumerator,
        InterruptDeviceEnumerator deviceEnumerator,
        CpuUsageMonitor? cpuUsageMonitor = null)
    {
        _service = service;
        _restartService = restartService;
        _treeEnumerator = treeEnumerator;
        _deviceEnumerator = deviceEnumerator;
        _cpuUsageMonitor = cpuUsageMonitor ?? new CpuUsageMonitor();

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _usageTimer.Tick += (_, _) => RefreshCoreUsage();
    }

    public void LoadIfNeeded()
    {
        if (!_loadedOnce)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Supplies where interrupts actually landed, averaged across every saved DPC/ISR trace. Without
    /// this the advice can only see CPU usage, which is blind to interrupt load — a core servicing 67%
    /// of the system's interrupts still reads ~0% busy, and recommending it would be exactly backwards.
    ///
    /// Deliberately an average over history rather than the newest trace: one trace is a short, noisy
    /// sample, so reading only the latest made the recommendation change every time a trace finished.
    /// </summary>
    public void SetInterruptLoads(AggregatedCoreLoads aggregate)
    {
        _interruptLoads = aggregate.Loads.Count > 0 ? aggregate.Loads : null;
        _interruptTraceCount = aggregate.TraceCount;
        _interruptTotalSeconds = aggregate.TotalSeconds;
    }

    /// <summary>Only poll CPU usage while this tab is actually visible — avoids a background timer ticking forever once the user navigates away.</summary>
    public void SetActive(bool isActive)
    {
        if (isActive)
        {
            _usageTimer.Start();
        }
        else
        {
            _usageTimer.Stop();
        }
    }

    /// <summary>Internal rather than private so a test can drive it directly without waiting on
    /// <see cref="_usageTimer"/>'s dispatcher tick — the logic under test doesn't depend on the timer,
    /// only its trigger does.</summary>
    internal void RefreshCoreUsage()
    {
        var usage = _cpuUsageMonitor.SampleUsagePercent();
        if (usage is null)
        {
            // A failed read, or one with no baseline yet, must not be treated as "every core is
            // idle" — leave whatever was last actually measured (or the initial "collecting data"
            // state) on screen rather than overwrite it with zeros that look like a reading but
            // aren't one. The advisor and the optimizer both already treat "no usage data" as
            // license to fall back to interrupt-trace data or say so outright, so skipping this tick
            // is enough; nothing downstream needs to be told about the failure specifically.
            return;
        }

        foreach (var controller in Controllers)
        {
            foreach (var core in controller.Cores)
            {
                if (core.Index < usage.Length)
                {
                    core.UsagePercent = usage[core.Index];
                }
            }

            controller.UpdateCoreAdvice(usage, _interruptLoads);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var (controllers, tree, extraDevices) = await Task.Run(() =>
                (_service.ListHostControllers(), _treeEnumerator.EnumerateHostControllers(), _deviceEnumerator.EnumerateInterruptCapableDevices()));
            Controllers.Clear();
            var number = 1;
            foreach (var info in controllers)
            {
                var treeNode = tree.FirstOrDefault(n => string.Equals(n.InstanceId, info.InstanceId, StringComparison.OrdinalIgnoreCase));
                var deviceNames = treeNode?.GetAttachedPhysicalDeviceNames() ?? [];
                Controllers.Add(new HostControllerViewModel(info, _service, _restartService, deviceNames, number));
                number++;
            }

            foreach (var (category, labelPrefix, restartCategory) in ExtraDeviceCategories)
            {
                var categoryNumber = 1;
                foreach (var device in extraDevices.Where(d => string.Equals(d.CategoryLabel, category, StringComparison.Ordinal)))
                {
                    var info = _service.ReadPolicy(device.InstanceId, device.FriendlyName);
                    Controllers.Add(new HostControllerViewModel(info, _service, _restartService, [device.FriendlyName], categoryNumber, labelPrefix, restartCategory));
                    categoryNumber++;
                }
            }

            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read host controllers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// One click, every device: picks a concrete core for each controller instead of just describing
    /// one. The assignment is deterministic — clicking it twice gives the same result — because it's
    /// driven by the last DPC/ISR trace (stable data) or, with no trace, a fixed round-robin spread.
    /// It tracks which cores this pass already gave to an earlier device so they spread across cores
    /// instead of every device landing on the same one (see CoreAffinityOptimizer).
    /// </summary>
    [RelayCommand]
    private void OptimizeAll()
    {
        if (Controllers.Count == 0)
        {
            OptimizeStatusMessage = "Nothing to optimize yet — click Refresh first.";
            return;
        }

        var claimedCores = new HashSet<int>();
        var pinnedCount = 0;
        var failedCount = 0;

        foreach (var controller in Controllers)
        {
            var recommended = CoreAffinityOptimizer.RecommendCore(controller.Cores.Count, _interruptLoads, claimedCores);
            if (recommended is int core)
            {
                if (controller.PinToCore(core))
                {
                    pinnedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
        }

        // Every write failing at once almost always means the app isn't elevated — say that plainly
        // instead of reporting a cheerful "optimized 0 devices".
        if (pinnedCount == 0 && failedCount > 0)
        {
            OptimizeStatusMessage = "Couldn't write any changes — affinity changes need administrator rights. Close the app and reopen it as administrator, then try again.";
            return;
        }

        // The trace count is stated outright because it's the honest measure of how much this
        // recommendation is worth: one short trace is a noisy sample, and the user deserves to know
        // that rather than being handed a confident-sounding answer built on 5 seconds of data.
        var basis = _interruptLoads is { Count: > 0 }
            ? $"from {_interruptTraceCount} saved DPC/ISR trace(s) totalling {_interruptTotalSeconds:0}s — each pinned to its own quiet core. "
              + (_interruptTraceCount < RecommendedTraceCount
                  ? $"Save at least {RecommendedTraceCount} traces (ideally while gaming or under load) for a more reliable average"
                  : "Clicking again gives the same result")
            : "spread evenly across cores (no saved DPC/ISR trace with core data yet). This is stable — run and SAVE a few traces on the DPC/ISR tab, then Optimize again for placement based on where interrupts actually land";
        var failedNote = failedCount > 0 ? $" ({failedCount} couldn't be written)" : string.Empty;
        OptimizeStatusMessage = $"Optimized {pinnedCount} device(s) {basis}.{failedNote} Restart each device (or reboot) to apply.";
    }

    /// <summary>
    /// Saves whatever cores are currently ticked on every card in one click, instead of clicking Apply
    /// on each one individually. Only touches devices with at least one core actually selected — a card
    /// nobody touched (still on Windows' default) is left alone rather than failed with "select a core".
    /// </summary>
    [RelayCommand]
    private void ApplyAll()
    {
        var candidates = Controllers.Where(c => c.HasCoreSelection).ToList();
        if (candidates.Count == 0)
        {
            OptimizeStatusMessage = "Nothing to apply — select cores on a device first, or use \"Optimize all\".";
            return;
        }

        var applied = 0;
        var failed = 0;
        foreach (var controller in candidates)
        {
            if (controller.TryApplyCurrentSelection())
            {
                applied++;
            }
            else
            {
                failed++;
            }
        }

        OptimizeStatusMessage = failed == 0
            ? $"Applied {applied} device(s). Restart each device (or reboot) to make the change live."
            : $"Applied {applied} device(s), {failed} failed — see each card's status. If every one failed, the app likely isn't running as administrator.";
    }

    /// <summary>
    /// Restarts every device whose category this app considers safe to restart live (see
    /// DeviceRestartService) in one click — a GPU still prompts its own confirmation per device, since
    /// that risk (a black screen for a few seconds) is real and belongs to the user's explicit "yes"
    /// each time, not a blanket one. Storage controllers are never included here regardless: restarting
    /// one live can hang a machine that boots from it, so this app never offers that button for storage
    /// at all, batch action or not.
    /// </summary>
    [RelayCommand]
    private void RestartAll()
    {
        var candidates = Controllers.Where(c => c.CanRestart).ToList();
        if (candidates.Count == 0)
        {
            OptimizeStatusMessage = "Nothing here can be restarted live — the rest need a reboot to take effect.";
            return;
        }

        var restarted = 0;
        var skipped = 0;
        foreach (var controller in candidates)
        {
            if (controller.TryRestartNow())
            {
                restarted++;
            }
            else
            {
                skipped++;
            }
        }

        OptimizeStatusMessage = skipped == 0
            ? $"Restarted {restarted} device(s) — changes are live now."
            : $"Restarted {restarted} device(s), {skipped} skipped or failed — see each card's status.";
    }
}
