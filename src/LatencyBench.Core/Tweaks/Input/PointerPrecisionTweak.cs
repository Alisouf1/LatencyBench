using System.Collections.Generic;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Input;

/// <summary>
/// Turns off "Enhance pointer precision" — Windows' mouse acceleration curve — so movement is 1:1.
/// </summary>
public class PointerPrecisionTweak : ControlPanelStringTweak
{
    /// <summary>
    /// All three values are needed. MouseSpeed alone selects the acceleration curve, but the two
    /// thresholds are what it is applied against, and leaving them set means a later Windows or
    /// driver change can re-enable acceleration from values this tweak claimed to have handled.
    /// The defaults are the ones a clean Windows profile ships with.
    /// </summary>
    private static readonly ManagedValue[] ManagedValues =
    {
        new("MouseSpeed", "0", "1"),
        new("MouseThreshold1", "0", "6"),
        new("MouseThreshold2", "0", "10"),
    };

    public PointerPrecisionTweak(TweakBackupStore backupStore)
        : base(backupStore)
    {
    }

    public override TweakDefinition Definition { get; } = new TweakDefinition
    {
        Id = "mouse.pointer-precision",
        Category = TweakCategory.Mouse,
        Name = "Disable pointer acceleration",
        Description = "Turns off \"Enhance pointer precision\" so mouse movement is 1:1 with no OS-level acceleration curve.",
        Risk = TweakRisk.Safe
    };

    protected override string SubKeyPath => @"Control Panel\Mouse";

    protected override IReadOnlyList<ManagedValue> Values => ManagedValues;
}
