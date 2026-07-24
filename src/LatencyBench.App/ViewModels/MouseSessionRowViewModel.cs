using CommunityToolkit.Mvvm.ComponentModel;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.App.ViewModels;

/// <summary>Wraps a saved session with the UI-only "pick for compare" checkbox state.</summary>
public sealed partial class MouseSessionRowViewModel(MouseTestSession session) : ObservableObject
{
    public MouseTestSession Session { get; } = session;

    [ObservableProperty]
    private bool _isSelectedForCompare;
}
