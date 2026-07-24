using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;

namespace LatencyBench.App.ViewModels;

public sealed partial class InterruptDeviceViewModel : ObservableObject
{
    private readonly InterruptDeviceService _service;
    private readonly DeviceRestartService _restartService;
    private readonly Action _onChanged;

    public static IReadOnlyList<InterruptPriority> AvailablePriorities { get; } =
        [InterruptPriority.Undefined, InterruptPriority.Low, InterruptPriority.Normal, InterruptPriority.High];

    public string InstanceId { get; }

    /// <summary>Real product name for GPU/network/audio/storage; for USB controllers this is the attached device names (or "USB Controller N") — never the raw chipset name.</summary>
    public string DeviceLabel { get; }

    public string CategoryLabel { get; }

    /// <summary>The kernel driver behind this device (e.g. "nvlddmkm"), when known — lets a DPC/ISR trace's top driver be matched back to this specific device.</summary>
    public string? ServiceName { get; }

    [ObservableProperty]
    private bool _isMsiEnabled;

    [ObservableProperty]
    private InterruptPriority _selectedPriority;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasPendingChange;

    /// <summary>Whether restarting this device is a recoverable blip rather than a risk to the running system.</summary>
    public bool CanRestart { get; }

    /// <summary>Explains why a restart isn't offered, for the categories where it's unsafe.</summary>
    public string RestartHint { get; }

    public InterruptDeviceViewModel(
        InterruptDeviceInfo info, string deviceLabel, InterruptDeviceService service, DeviceRestartService restartService, Action onChanged)
    {
        _service = service;
        _restartService = restartService;
        _onChanged = onChanged;
        InstanceId = info.InstanceId;
        DeviceLabel = deviceLabel;
        CategoryLabel = info.CategoryLabel;
        ServiceName = info.ServiceName;
        _isMsiEnabled = info.IsMsiEnabled;
        _selectedPriority = info.Priority;

        CanRestart = DeviceRestartService.IsRestartable(info.CategoryLabel);
        RestartHint = DeviceRestartService.RequiresConfirmation(info.CategoryLabel)
            ? "Applies the change now instead of waiting for a reboot. Your screen will blank for several seconds while the display driver reloads — you'll be asked to confirm first."
            : CanRestart
                ? "Restarts this device so the change takes effect now, instead of waiting for a reboot. It will drop out briefly."
                : $"Restarting a {info.CategoryLabel.ToLowerInvariant()} from here can hang the machine — it may hold the boot volume — so this change genuinely needs a reboot.";
    }

    [RelayCommand]
    private void ToggleMsi()
    {
        var next = !IsMsiEnabled;
        try
        {
            _service.SetMsiMode(InstanceId, next);
            IsMsiEnabled = next;
            MarkPending();
            _onChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
    }

    partial void OnSelectedPriorityChanged(InterruptPriority value)
    {
        try
        {
            _service.SetPriority(InstanceId, value);
            MarkPending();
            _onChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Restart()
    {
        if (DeviceRestartService.RequiresConfirmation(CategoryLabel) && !ConfirmRiskyRestart())
        {
            return;
        }

        try
        {
            _restartService.Restart(InstanceId);
            HasPendingChange = false;
            StatusMessage = "Restarted — the change is live now. Re-run a port test to measure the difference.";
            _onChanged();
        }
        catch (Exception ex)
        {
            // Surface the real OS error rather than a guess; a reboot still applies the change.
            StatusMessage = $"Restart failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Restarting the display adapter is recoverable in the normal case but can, rarely, leave the
    /// screen dark until a forced power-off. That risk belongs to the user, so it's spelled out
    /// plainly rather than buried — no "are you sure?" without saying what could actually happen.
    /// </summary>
    private bool ConfirmRiskyRestart()
    {
        var answer = MessageBox.Show(
            $"Restarting {DeviceLabel} will blank your screen for several seconds while the display driver reloads.\n\n" +
            "It normally comes back on its own — this is the same thing Device Manager does when you disable and re-enable the device. " +
            "But if it doesn't recover, the only way out is holding the power button, and anything unsaved in other apps is lost.\n\n" +
            "Save your work first. Restart it now?",
            "Restart graphics device?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    private void MarkPending()
    {
        HasPendingChange = true;
        StatusMessage = CanRestart
            ? "Saved — not active yet. Click \"Restart device\" to apply it now."
            : "Saved — takes effect after a reboot.";
    }
}
