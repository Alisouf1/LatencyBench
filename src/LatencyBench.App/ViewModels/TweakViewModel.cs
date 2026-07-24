using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.App.ViewModels;

public sealed partial class TweakViewModel : ObservableObject
{
    private readonly ITweak _tweak;

    public TweakDefinition Definition => _tweak.Definition;

    [ObservableProperty]
    private TweakState _state;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    public TweakViewModel(ITweak tweak)
    {
        _tweak = tweak;
        _state = TweakState.Unknown;
    }

    public void RefreshState()
    {
        try
        {
            State = _tweak.GetState();
        }
        catch (Exception ex)
        {
            State = TweakState.Unknown;
            StatusMessage = $"Could not read current state: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        IsBusy = true;
        try
        {
            _tweak.Apply();
            RefreshState();
            StatusMessage = Definition.RequiresReboot
                ? "Applied. Restart your PC for it to take effect."
                : "Applied.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't apply: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        IsBusy = true;
        try
        {
            _tweak.Revert();
            RefreshState();
            StatusMessage = Definition.RequiresReboot
                ? "Reverted. Restart your PC for it to take effect."
                : "Reverted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't revert: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
