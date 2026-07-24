using CommunityToolkit.Mvvm.ComponentModel;

namespace LatencyBench.App.ViewModels;

public sealed partial class CoreToggleViewModel : ObservableObject
{
    public int Index { get; }

    public string Label => $"C{Index}";

    /// <summary>The device(s) that would use this core's interrupts — the controller's AttachedDevicesSummary, shown as a tooltip.</summary>
    public string DeviceHint { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private double _usagePercent;

    public CoreToggleViewModel(int index, bool isSelected, string deviceHint)
    {
        Index = index;
        _isSelected = isSelected;
        DeviceHint = deviceHint;
    }
}
