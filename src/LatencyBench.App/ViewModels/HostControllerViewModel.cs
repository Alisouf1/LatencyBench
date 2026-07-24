using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;

namespace LatencyBench.App.ViewModels;

public sealed partial class HostControllerViewModel : ObservableObject
{
    private readonly InterruptAffinityService _service;
    private readonly DeviceRestartService _restartService;
    private readonly string _restartCategory;

    /// <summary>Whether this device's category is in DeviceRestartService's restartable set — computed rather than hardcoded so a future change to that categorization is reflected here too.</summary>
    public bool CanRestart { get; }

    public string InstanceId { get; }

    /// <summary>The full technical chipset name (e.g. "AMD USB 3.10 eXtensible Host Controller - 1.20 (Microsoft)") — kept for reference/tooltips, not shown as the primary label.</summary>
    public string FriendlyName { get; }

    /// <summary>Short label distinguishing this controller from the others, e.g. "USB Controller 2" — used instead of the verbose chipset name.</summary>
    public string ControllerLabel { get; }

    /// <summary>The number in ControllerLabel, as data — lets other tabs (e.g. the Dashboard's unified advisor) match this controller against a port's "Controller N, port P" location without re-parsing the label string.</summary>
    public int ControllerNumber { get; }

    /// <summary>The real product name(s) of whatever's plugged into this controller (e.g. "MCHOSE M7 Ultra"), or the device's own friendly name for non-USB categories (GPU, storage, audio), or a fallback note if nothing's attached.</summary>
    public string AttachedDevicesSummary { get; }

    public bool HasAttachedDevices { get; }

    public ObservableCollection<CoreToggleViewModel> Cores { get; }

    [ObservableProperty]
    private string _policySummary;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _coreAdvice = "Collecting core usage data…";

    private IReadOnlyList<double> _lastCoreUsagePercent = [];
    private IReadOnlyList<CoreLoad>? _lastInterruptLoads;

    /// <param name="labelPrefix">Human-readable category shown before the number, e.g. "USB Controller", "GPU", "Storage Controller", "Audio Device".</param>
    /// <param name="restartCategory">The exact category key <see cref="DeviceRestartService"/> uses (e.g. "USB controller", "GPU") — separate from <paramref name="labelPrefix"/> because the two don't always share casing/wording.</param>
    public HostControllerViewModel(
        HostControllerInfo info,
        InterruptAffinityService service,
        DeviceRestartService restartService,
        IReadOnlyList<string> attachedDeviceNames,
        int controllerNumber,
        string labelPrefix = "USB Controller",
        string restartCategory = "USB controller")
    {
        _service = service;
        _restartService = restartService;
        _restartCategory = restartCategory;
        InstanceId = info.InstanceId;
        FriendlyName = info.FriendlyName;
        ControllerNumber = controllerNumber;
        ControllerLabel = $"{labelPrefix} {controllerNumber}";
        CanRestart = DeviceRestartService.IsRestartable(restartCategory);
        HasAttachedDevices = attachedDeviceNames.Count > 0;
        AttachedDevicesSummary = attachedDeviceNames.Count == 0
            ? "No devices attached"
            : string.Join(", ", attachedDeviceNames);

        var selectedCores = info.Policy == InterruptAffinityPolicy.SpecifiedProcessors
            ? new HashSet<int>(AffinityMask.ToCoreIndices(info.AffinityMask))
            : [];

        Cores = new ObservableCollection<CoreToggleViewModel>(
            Enumerable.Range(0, info.LogicalProcessorCount)
                .Select(i => new CoreToggleViewModel(i, selectedCores.Contains(i), AttachedDevicesSummary)));

        _policySummary = DescribePolicy(info.Policy);
    }

    /// <summary>Recomputes the core-pinning recommendation from the latest system-wide per-core usage — called every second while the Affinity tab is visible, and right after Apply/Clear so the advice reflects the change immediately.</summary>
    /// <param name="interruptLoads">Where interrupts actually landed in the last DPC/ISR trace, when one has been run. This is the signal that matters; CPU usage alone can't see interrupt load.</param>
    public void UpdateCoreAdvice(IReadOnlyList<double> systemCoreUsagePercent, IReadOnlyList<CoreLoad>? interruptLoads = null)
    {
        _lastCoreUsagePercent = systemCoreUsagePercent;
        _lastInterruptLoads = interruptLoads;
        var pinned = Cores.Where(c => c.IsSelected).Select(c => c.Index).ToList();
        CoreAdvice = CoreAffinityAdvisor.Generate(systemCoreUsagePercent, pinned, interruptLoads);
    }

    /// <summary>Used by "Optimize all" to apply a single recommended core without going through the UI toggles — selects exactly that core, then reuses the same save path (and status/advice refresh) a manual Apply click would.</summary>
    /// <returns>true only if the change was actually written to the registry — so the caller's count reflects real successes, not attempts (a non-elevated run, or a device that refuses the write, returns false here).</returns>
    public bool PinToCore(int coreIndex)
    {
        foreach (var core in Cores)
        {
            core.IsSelected = core.Index == coreIndex;
        }

        return TrySaveSelectedCores();
    }

    /// <summary>Whether the user has picked at least one core here (regardless of whether it's been saved yet) — lets "Apply all" skip devices nobody touched instead of failing them with "select at least one core".</summary>
    public bool HasCoreSelection => Cores.Any(c => c.IsSelected);

    /// <summary>Public entry point for "Apply all" — same save path as the per-device Apply button.</summary>
    public bool TryApplyCurrentSelection() => TrySaveSelectedCores();

    /// <summary>Public entry point for "Restart all" — same restart path (including the GPU confirmation prompt) as the per-device Restart button.</summary>
    public bool TryRestartNow() => TryRestartCore();

    private static string DescribePolicy(InterruptAffinityPolicy policy) => policy switch
    {
        InterruptAffinityPolicy.MachineDefault => "Using Windows default core assignment",
        InterruptAffinityPolicy.SpecifiedProcessors => "Pinned to specific cores",
        _ => policy.ToString(),
    };

    [RelayCommand]
    private void Apply() => TrySaveSelectedCores();

    /// <summary>The shared save path behind both the Apply button and "Optimize all" — writes the currently-selected cores to the registry and refreshes status/advice.</summary>
    /// <returns>true if the write succeeded; false if nothing was selected or the OS rejected the write (StatusMessage explains which).</returns>
    private bool TrySaveSelectedCores()
    {
        var selected = Cores.Where(c => c.IsSelected).Select(c => c.Index).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Select at least one core before applying.";
            return false;
        }

        try
        {
            _service.SetSpecifiedCores(InstanceId, selected);
            PolicySummary = DescribePolicy(InterruptAffinityPolicy.SpecifiedProcessors);
            StatusMessage = CanRestart
                ? "Saved — not active yet. Click \"Restart device\" to apply it now."
                : "Saved — takes effect after a reboot.";
            UpdateCoreAdvice(_lastCoreUsagePercent, _lastInterruptLoads);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private void ClearOverride()
    {
        try
        {
            _service.ClearOverride(InstanceId);
            foreach (var core in Cores)
            {
                core.IsSelected = false;
            }

            PolicySummary = DescribePolicy(InterruptAffinityPolicy.MachineDefault);
            StatusMessage = CanRestart
                ? "Reverted to Windows default — not active yet. Click \"Restart device\" to apply it now."
                : "Reverted to Windows default — takes effect after a reboot.";
            UpdateCoreAdvice(_lastCoreUsagePercent, _lastInterruptLoads);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not clear: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Restart() => TryRestartCore();

    /// <summary>The shared restart path behind both the Restart button and "Restart all".</summary>
    /// <returns>true if the device was actually restarted; false if the user declined the GPU confirmation prompt or the OS refused the restart (StatusMessage explains which).</returns>
    private bool TryRestartCore()
    {
        if (DeviceRestartService.RequiresConfirmation(_restartCategory) && !ConfirmRiskyRestart())
        {
            return false;
        }

        try
        {
            _restartService.Restart(InstanceId);
            StatusMessage = "Restarted — the change is live now. Re-run a port test or DPC/ISR trace to measure the difference.";
            return true;
        }
        catch (Exception ex)
        {
            // Surface the real OS error rather than a guess; a reboot still applies the change.
            StatusMessage = $"Restart failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Restarting a GPU is recoverable in the normal case but can, rarely, leave the screen dark until a forced power-off — that risk belongs to the user, so it's spelled out plainly rather than buried behind a generic "are you sure?".</summary>
    private bool ConfirmRiskyRestart()
    {
        var answer = MessageBox.Show(
            $"Restarting {FriendlyName} will blank your screen for several seconds while the display driver reloads.\n\n" +
            "It normally comes back on its own — this is the same thing Device Manager does when you disable and re-enable the device. " +
            "But if it doesn't recover, the only way out is holding the power button, and anything unsaved in other apps is lost.\n\n" +
            "Save your work first. Restart it now?",
            "Restart graphics device?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }
}
