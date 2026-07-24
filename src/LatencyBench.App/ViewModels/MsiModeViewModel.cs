using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class MsiModeViewModel : ObservableObject
{
    private readonly InterruptDeviceService _service;
    private readonly DeviceRestartService _restartService;
    private readonly InterruptDeviceEnumerator _deviceEnumerator;
    private readonly UsbTreeEnumerator _treeEnumerator;
    private bool _loadedOnce;

    public ObservableCollection<InterruptDeviceViewModel> Devices { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _advisorMessage = InterruptDeviceAdvisor.Generate([]);

    /// <summary>Changes that are saved to the registry but not live yet — easy to forget without a reminder.</summary>
    [ObservableProperty]
    private string? _pendingChangesMessage;

    public MsiModeViewModel(
        InterruptDeviceService service,
        DeviceRestartService restartService,
        InterruptDeviceEnumerator deviceEnumerator,
        UsbTreeEnumerator treeEnumerator)
    {
        _service = service;
        _restartService = restartService;
        _deviceEnumerator = deviceEnumerator;
        _treeEnumerator = treeEnumerator;
    }

    public void LoadIfNeeded()
    {
        if (!_loadedOnce)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var (infos, tree) = await Task.Run(() => (_deviceEnumerator.EnumerateInterruptCapableDevices(), _treeEnumerator.EnumerateHostControllers()));

            var controllerNumbers = tree
                .Select((hc, i) => (hc.InstanceId, Number: i + 1))
                .ToDictionary(x => x.InstanceId, x => x.Number, StringComparer.OrdinalIgnoreCase);

            Devices.Clear();
            foreach (var info in infos)
            {
                var label = ResolveLabel(info, tree, controllerNumbers);
                Devices.Add(new InterruptDeviceViewModel(info, label, _service, _restartService, RefreshAdvisorMessage));
            }

            _loadedOnce = true;
            RefreshAdvisorMessage();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read interrupt-capable devices: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>USB controllers show what's plugged into them (real product names) rather than the raw chipset name; every other category's own FriendlyName is already the real product name (e.g. "NVIDIA GeForce RTX 4070").</summary>
    private static string ResolveLabel(InterruptDeviceInfo info, IReadOnlyList<UsbDeviceNode> tree, Dictionary<string, int> controllerNumbers)
    {
        if (info.CategoryLabel != "USB controller")
        {
            return info.FriendlyName;
        }

        var treeNode = tree.FirstOrDefault(n => string.Equals(n.InstanceId, info.InstanceId, StringComparison.OrdinalIgnoreCase));
        var deviceNames = treeNode?.GetAttachedPhysicalDeviceNames() ?? [];
        return deviceNames.Count > 0
            ? string.Join(", ", deviceNames)
            : $"USB Controller {controllerNumbers.GetValueOrDefault(info.InstanceId, 0)}";
    }

    private void RefreshAdvisorMessage()
    {
        var infos = Devices
            .Select(d => new InterruptDeviceInfo(d.InstanceId, d.DeviceLabel, d.CategoryLabel, d.IsMsiEnabled, d.SelectedPriority))
            .ToList();
        AdvisorMessage = InterruptDeviceAdvisor.Generate(infos);
        RefreshPendingChanges();
    }

    private void RefreshPendingChanges()
    {
        var pending = Devices.Where(d => d.HasPendingChange).ToList();
        if (pending.Count == 0)
        {
            PendingChangesMessage = null;
            return;
        }

        var needReboot = pending.Where(d => !d.CanRestart).Select(d => d.DeviceLabel).ToList();
        var restartable = pending.Where(d => d.CanRestart).Select(d => d.DeviceLabel).ToList();

        var parts = new List<string>();
        if (restartable.Count > 0)
        {
            parts.Add($"{string.Join(", ", restartable)} — click \"Restart device\" to apply now.");
        }

        if (needReboot.Count > 0)
        {
            parts.Add($"{string.Join(", ", needReboot)} — needs a reboot.");
        }

        PendingChangesMessage = $"{pending.Count} change(s) saved but not active yet: {string.Join(" ", parts)}";
    }
}
