using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Processes;

namespace LatencyBench.App.ViewModels;

/// <summary>
/// Per-process priority, I/O priority and CPU affinity.
/// <para>
/// Every change here dies with the process. That is stated up front in the view rather than left
/// implicit, because "why did this reset after I relaunched the game" is the support question a
/// silent session-scoped change produces.
/// </para>
/// </summary>
public sealed partial class ProcessTuningViewModel : ObservableObject
{
    private readonly ProcessTuner _tuner;

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    [ObservableProperty]
    private bool _windowedOnly = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _status;

    private bool _loadedOnce;

    public ProcessTuningViewModel(ProcessTuner tuner)
    {
        _tuner = tuner;
    }

    public void LoadIfNeeded()
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        Refresh();
    }

    partial void OnWindowedOnlyChanged(bool value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        IsLoading = true;
        try
        {
            Processes.Clear();
            foreach (var candidate in _tuner.ListCandidates(WindowedOnly))
            {
                Processes.Add(new ProcessRowViewModel(candidate, this));
            }

            Status = $"{Processes.Count} process(es) listed.";
        }
        catch (Exception ex)
        {
            Status = $"Could not list processes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void SetPriority(ProcessRowViewModel row, ProcessPriorityClass priority)
    {
        try
        {
            _tuner.SetPriority(row.Id, priority);
            row.UpdatePriority(priority);
            Status = $"{row.DisplayName}: priority set to {priority}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not change priority for {row.DisplayName}: {ex.Message}";
        }
    }

    internal void SetIoPriority(ProcessRowViewModel row, IoPriority priority)
    {
        try
        {
            _tuner.SetIoPriority(row.Id, priority);
            row.UpdateIoPriority(priority);
            Status = $"{row.DisplayName}: I/O priority set to {priority}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not change I/O priority for {row.DisplayName}: {ex.Message}";
        }
    }

    internal void ClearAffinity(ProcessRowViewModel row)
    {
        try
        {
            _tuner.ClearAffinity(row.Id);
            row.UpdateAffinityCleared();
            Status = $"{row.DisplayName}: restored to all processors.";
        }
        catch (Exception ex)
        {
            Status = $"Could not clear affinity for {row.DisplayName}: {ex.Message}";
        }
    }
}

public sealed partial class ProcessRowViewModel : ObservableObject
{
    private readonly ProcessTuningViewModel _owner;

    public ProcessRowViewModel(TunableProcess process, ProcessTuningViewModel owner)
    {
        _owner = owner;
        Id = process.Id;
        Name = process.Name;
        DisplayName = process.DisplayName;
        IsAccessible = process.IsAccessible;
        _priority = process.PriorityClass;
        _ioPriority = process.IoPriority;
        HasCustomAffinity = IsRestricted(process.AffinityMask);
    }

    /// <summary>True when the mask excludes at least one logical processor Windows actually has.</summary>
    private static bool IsRestricted(ulong? mask)
    {
        if (mask is not { } value)
        {
            return false;
        }

        int processorCount = Environment.ProcessorCount;
        ulong allProcessorsMask = processorCount >= 64 ? ulong.MaxValue : (1UL << processorCount) - 1;
        return value != 0 && value != allProcessorsMask;
    }

    public int Id { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public bool IsAccessible { get; }

    [ObservableProperty]
    private ProcessPriorityClass? _priority;

    [ObservableProperty]
    private IoPriority? _ioPriority;

    [ObservableProperty]
    private bool _hasCustomAffinity;

    public string PriorityLabel => Priority?.ToString() ?? "Unknown";

    public string IoPriorityLabel => IoPriority?.ToString() ?? "Unknown";

    [RelayCommand]
    private void SetAboveNormal() => _owner.SetPriority(this, ProcessPriorityClass.AboveNormal);

    [RelayCommand]
    private void SetHigh() => _owner.SetPriority(this, ProcessPriorityClass.High);

    [RelayCommand]
    private void SetNormalPriority() => _owner.SetPriority(this, ProcessPriorityClass.Normal);

    [RelayCommand]
    private void SetHighIo() => _owner.SetIoPriority(this, Core.Processes.IoPriority.High);

    [RelayCommand]
    private void SetNormalIo() => _owner.SetIoPriority(this, Core.Processes.IoPriority.Normal);

    [RelayCommand]
    private void ClearAffinity() => _owner.ClearAffinity(this);

    internal void UpdatePriority(ProcessPriorityClass priority)
    {
        Priority = priority;
        OnPropertyChanged(nameof(PriorityLabel));
    }

    internal void UpdateIoPriority(IoPriority priority)
    {
        IoPriority = priority;
        OnPropertyChanged(nameof(IoPriorityLabel));
    }

    internal void UpdateAffinityCleared() => HasCustomAffinity = false;
}
