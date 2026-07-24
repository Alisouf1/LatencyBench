using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.App.ViewModels;

/// <summary>One physical port's combined results — its best run headlines the row, click to expand the full timestamped history.</summary>
public sealed partial class PortHistoryGroupViewModel : ObservableObject
{
    public PortRankResult Best { get; }

    public ObservableCollection<PortRankResult> History { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public PortHistoryGroupViewModel(PortHistoryGroup group)
    {
        Best = group.Best;
        History = new ObservableCollection<PortRankResult>(group.History);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}
