using LatencyBench.Core.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.App.ViewModels;

public sealed partial class TweaksViewModel : ObservableObject
{
    private static readonly TweakCategory[] CategoryOrder =
    [
        TweakCategory.Power,
        TweakCategory.Usb,
        TweakCategory.Mouse,
        TweakCategory.Keyboard,
        TweakCategory.Cpu,
        TweakCategory.Gpu,
        TweakCategory.Ssd,
        TweakCategory.Network,
        TweakCategory.Aggressive,
    ];

    private readonly RestorePointService _restorePointService;
    private bool _loadedOnce;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _restorePointStatus;

    [ObservableProperty]
    private string? _batchActionStatus;

    public ObservableCollection<TweakCategoryGroupViewModel> Groups { get; } = [];

    public TweaksViewModel(TweakCatalog catalog, RestorePointService restorePointService)
    {
        _restorePointService = restorePointService;

        var allTweaks = catalog.BuildAll().Select(t => new TweakViewModel(t)).ToList();
        foreach (var category in CategoryOrder)
        {
            var inCategory = allTweaks.Where(t => t.Definition.Category == category).ToList();
            if (inCategory.Count == 0)
            {
                continue;
            }

            Groups.Add(new TweakCategoryGroupViewModel
            {
                CategoryName = DisplayName(category),
                Tweaks = new ObservableCollection<TweakViewModel>(inCategory),
            });
        }
    }

    public void LoadIfNeeded()
    {
        if (!_loadedOnce)
        {
            Refresh();
            _loadedOnce = true;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        IsLoading = true;
        try
        {
            foreach (var tweak in Groups.SelectMany(g => g.Tweaks))
            {
                tweak.RefreshState();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CreateRestorePoint()
    {
        var (success, message) = _restorePointService.CreateRestorePoint("LatencyBench — before applying tweaks");
        RestorePointStatus = message;
    }

    /// <summary>
    /// Applies every tweak that isn't already applied, in one click, instead of clicking Apply on each
    /// one individually. Aggressive-risk tweaks (currently just disabling SysMain) are deliberately
    /// skipped here — a blanket batch action is exactly the kind of place a "disable this Windows
    /// service" change shouldn't happen without the extra attention of applying it by itself.
    ///
    /// Runs on the UI thread (each individual Apply is a quick registry write or a single external
    /// process call), but yields between tweaks so the window repaints and each status line appears
    /// progressively instead of the whole batch looking frozen until it's done.
    /// </summary>
    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        if (IsLoading)
        {
            // Never a silent refusal. Every control on this page binds IsEnabled to IsLoading, so a
            // flag that sticks leaves the button dead with no message - the same failure that made
            // Optimize's Re-analyse look unwired. Saying so costs nothing and removes the ambiguity.
            DiagnosticLog.Warn("Tweaks", "Apply-all refused: a previous batch is still in progress.");
            BatchActionStatus = "Still finishing the previous batch — try again in a moment.";
            return;
        }

        DiagnosticLog.Info("Tweaks", "Apply-all starting.");
        IsLoading = true;
        try
        {
            var applied = 0;
            var failed = 0;
            var skippedAggressive = 0;

            foreach (var tweak in Groups.SelectMany(g => g.Tweaks))
            {
                if (tweak.Definition.Risk == TweakRisk.Aggressive)
                {
                    skippedAggressive++;
                    continue;
                }

                if (tweak.State == TweakState.Applied)
                {
                    continue;
                }

                tweak.ApplyCommand.Execute(null);
                await Task.Yield();

                if (tweak.State == TweakState.Applied)
                {
                    applied++;
                }
                else
                {
                    failed++;
                }
            }

            var failedNote = failed > 0 ? $" {failed} failed — see each tweak's status." : string.Empty;
            var skippedNote = skippedAggressive > 0
                ? $" Skipped {skippedAggressive} aggressive tweak(s) — apply those individually."
                : string.Empty;
            BatchActionStatus = $"Applied {applied} tweak(s).{failedNote}{skippedNote}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Reverts every currently-applied tweak back to its default, including aggressive ones — reverting is always the safe direction, so there's no reason to exclude them the way ApplyAll does.</summary>
    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (IsLoading)
        {
            DiagnosticLog.Warn("Tweaks", "Revert-all refused: a previous batch is still in progress.");
            BatchActionStatus = "Still finishing the previous batch — try again in a moment.";
            return;
        }

        DiagnosticLog.Info("Tweaks", "Revert-all starting.");
        IsLoading = true;
        try
        {
            var reverted = 0;
            var failed = 0;

            foreach (var tweak in Groups.SelectMany(g => g.Tweaks))
            {
                if (tweak.State != TweakState.Applied)
                {
                    continue;
                }

                tweak.RevertCommand.Execute(null);
                await Task.Yield();

                if (tweak.State != TweakState.Applied)
                {
                    reverted++;
                }
                else
                {
                    failed++;
                }
            }

            BatchActionStatus = failed == 0
                ? $"Reverted {reverted} tweak(s) to default."
                : $"Reverted {reverted} tweak(s) to default. {failed} failed — see each tweak's status.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string DisplayName(TweakCategory category) => category switch
    {
        TweakCategory.Usb => "USB",
        TweakCategory.Cpu => "CPU",
        TweakCategory.Gpu => "GPU",
        TweakCategory.Ssd => "SSD",
        TweakCategory.Aggressive => "Aggressive — use with care",
        _ => category.ToString(),
    };
}
