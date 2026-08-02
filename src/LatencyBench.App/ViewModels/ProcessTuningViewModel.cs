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
            row.RowError = null;
            Status = $"{row.DisplayName}: priority set to {priority}.";
        }
        catch (Exception ex)
        {
            // Errors specific to one process are shown on that row rather than here — a page-level
            // message about one game among a dozen listed processes is easy to miss which row it was
            // even about, and stale once you look at a different row.
            row.RowError = ex.Message;
        }
    }

    internal void SetIoPriority(ProcessRowViewModel row, IoPriority priority)
    {
        try
        {
            _tuner.SetIoPriority(row.Id, priority);
            row.UpdateIoPriority(priority);
            row.RowError = null;
            Status = $"{row.DisplayName}: I/O priority set to {priority}.";
        }
        catch (Exception ex)
        {
            row.RowError = ex.Message;
        }
    }

    internal void ClearAffinity(ProcessRowViewModel row)
    {
        try
        {
            _tuner.ClearAffinity(row.Id);
            row.UpdateAffinityCleared();
            row.RowError = null;
            Status = $"{row.DisplayName}: restored to all processors.";
        }
        catch (Exception ex)
        {
            row.RowError = ex.Message;
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
        CanModify = process.CanModify;
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

    /// <summary>Whether priority/I/O priority/affinity can plausibly be changed at all for this
    /// process — see <see cref="TunableProcess.CanModify"/>. Always false when
    /// <see cref="IsAccessible"/> is false.</summary>
    public bool CanModify { get; }

    /// <summary>True specifically when this process is readable but not writable — the
    /// anti-cheat-shaped case, distinct from <see cref="IsAccessible"/> being false outright.</summary>
    public bool IsWriteProtected => IsAccessible && !CanModify;

    /// <summary>Explains, for a tooltip on the disabled action buttons, why they are disabled — null
    /// when they are not (nothing shown in that case).</summary>
    public string? DisabledReason => !IsAccessible
        ? "Not accessible — running at a higher integrity level."
        : !CanModify
            ? "Priority, I/O priority and affinity can't be changed for this process. It appears to be " +
              "protected — commonly by anti-cheat software (e.g. EasyAntiCheat, BattlEye, Vanguard)."
            : null;

    [ObservableProperty]
    private ProcessPriorityClass? _priority;

    [ObservableProperty]
    private IoPriority? _ioPriority;

    [ObservableProperty]
    private bool _hasCustomAffinity;

    /// <summary>The error from the most recent failed action on this process specifically — shown
    /// inline on this row rather than as a page-level message, and cleared on the next successful
    /// action.</summary>
    [ObservableProperty]
    private string? _rowError;

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
