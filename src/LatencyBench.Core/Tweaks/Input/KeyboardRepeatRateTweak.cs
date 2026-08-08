using System.Collections.Generic;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Input;

/// <summary>
/// Sets the keyboard repeat delay to its shortest and the repeat rate to its fastest.
/// </summary>
public class KeyboardRepeatRateTweak : ControlPanelStringTweak
{
    /// <summary>
    /// KeyboardSpeed's applied value and Windows default are both 31 deliberately: 31 already IS the
    /// maximum Windows exposes, so this tweak only really changes the delay. It stays in the list so
    /// the state check confirms both halves rather than reporting "fastest repeat rate" on a profile
    /// where something had lowered the speed.
    /// </summary>
    private static readonly ManagedValue[] ManagedValues =
    {
        new("KeyboardDelay", "0", "1"),
        new("KeyboardSpeed", "31", "31"),
    };

    public KeyboardRepeatRateTweak(TweakBackupStore backupStore)
        : base(backupStore)
    {
    }

    public override TweakDefinition Definition { get; } = new TweakDefinition
    {
        Id = "keyboard.repeat-rate",
        Category = TweakCategory.Keyboard,
        Name = "Fastest repeat rate",
        Description = "Sets keyboard repeat delay to shortest and repeat rate to fastest (Windows' own maximum, not a driver-level override).",
        Risk = TweakRisk.Safe
    };

    protected override string SubKeyPath => @"Control Panel\Keyboard";

    protected override IReadOnlyList<ManagedValue> Values => ManagedValues;
}
