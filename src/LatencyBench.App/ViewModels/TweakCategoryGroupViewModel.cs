using System.Collections.ObjectModel;

namespace LatencyBench.App.ViewModels;

public sealed class TweakCategoryGroupViewModel
{
    public required string CategoryName { get; init; }
    public required ObservableCollection<TweakViewModel> Tweaks { get; init; }
}
