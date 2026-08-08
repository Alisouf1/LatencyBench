using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Input;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the two Control Panel input tweaks and the base class they now share. Nothing here touches
/// the user's real mouse or keyboard settings.
///
/// These two were byte-for-byte the same logic with a different key path and value list. Merging them
/// removes the risk that a fix to one silently leaves the other wrong - the exact divergence that had
/// already happened between InterruptDeviceService and InterruptAffinityService in this codebase.
/// </summary>
public sealed class ControlPanelInputTweakTests : IDisposable
{
    private readonly string _directory;
    private readonly TweakBackupStore _backupStore;

    public ControlPanelInputTweakTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchInput", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _backupStore = new TweakBackupStore(Path.Combine(_directory, "tweak-backups.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Holds the HKCU values in memory. Generic over both concrete tweaks.</summary>
    private sealed class FakeMouse : PointerPrecisionTweak
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public FakeMouse(TweakBackupStore store, bool keyMissing = false) : base(store) =>
            KeyMissing = keyMissing;

        public bool KeyMissing { get; }

        public void Seed(string name, string value) => _values[name] = value;

        public string? Peek(string name) => _values.GetValueOrDefault(name);

        protected override IReadOnlyDictionary<string, string?> ReadValues()
        {
            if (KeyMissing)
            {
                return new Dictionary<string, string?>();
            }

            return new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }
                .ToDictionary(n => n, n => _values.GetValueOrDefault(n), StringComparer.Ordinal);
        }

        protected override void WriteValue(string valueName, string value) => _values[valueName] = value;
    }

    private sealed class FakeKeyboard : KeyboardRepeatRateTweak
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public FakeKeyboard(TweakBackupStore store) : base(store) { }

        public void Seed(string name, string value) => _values[name] = value;

        public string? Peek(string name) => _values.GetValueOrDefault(name);

        protected override IReadOnlyDictionary<string, string?> ReadValues() =>
            new[] { "KeyboardDelay", "KeyboardSpeed" }
                .ToDictionary(n => n, n => _values.GetValueOrDefault(n), StringComparer.Ordinal);

        protected override void WriteValue(string valueName, string value) => _values[valueName] = value;
    }

    // --- Backup key compatibility ----------------------------------------------------------------

    /// <summary>
    /// The backup key format must be byte-identical to what the previous implementation produced, or
    /// every backup already recorded on a user's machine is orphaned and their original mouse and
    /// keyboard settings become unrecoverable through the app.
    /// </summary>
    [Fact]
    public void TheBackupKeyFormatIsUnchangedFromBeforeTheRefactor()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "1");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();

        // Exactly the string the old implementation built: "registry:HKCU\\Control Panel\\Mouse\\" + name
        Assert.Equal("1", _backupStore.TryGet(@"registry:HKCU\Control Panel\Mouse\MouseSpeed"));
        Assert.Equal("6", _backupStore.TryGet(@"registry:HKCU\Control Panel\Mouse\MouseThreshold1"));
    }

    [Fact]
    public void AKeyboardBackupWrittenByTheOldFormatIsStillHonoured()
    {
        // Simulates a backup recorded by a previous version of the app.
        _backupStore.Save(@"registry:HKCU\Control Panel\Keyboard\KeyboardDelay", "2");

        var keyboard = new FakeKeyboard(_backupStore);
        keyboard.Seed("KeyboardDelay", "0");
        keyboard.Seed("KeyboardSpeed", "31");

        keyboard.Revert();

        Assert.Equal("2", keyboard.Peek("KeyboardDelay"));
    }

    // --- State -----------------------------------------------------------------------------------

    [Fact]
    public void StateIsAppliedWhenEveryManagedValueMatches()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "0");
        mouse.Seed("MouseThreshold1", "0");
        mouse.Seed("MouseThreshold2", "0");

        Assert.Equal(TweakState.Applied, mouse.GetState());
    }

    [Fact]
    public void OneUnsetThresholdMakesItNotApplied()
    {
        // MouseSpeed alone selects the acceleration curve, but the thresholds are what it is applied
        // against. Reporting Applied with a threshold still set would claim acceleration was off when
        // a later Windows or driver change can bring it back.
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "0");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "0");

        Assert.Equal(TweakState.NotApplied, mouse.GetState());
    }

    [Fact]
    public void AnAbsentValueIsNotAppliedRatherThanThrowing()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "0");
        // The two thresholds are absent entirely.

        Assert.Equal(TweakState.NotApplied, mouse.GetState());
    }

    [Fact]
    public void StateIsUnknownWhenTheKeyItselfCannotBeOpened()
    {
        // Not the same as "not applied" - nothing was read at all.
        Assert.Equal(TweakState.Unknown, new FakeMouse(_backupStore, keyMissing: true).GetState());
    }

    // --- Apply -----------------------------------------------------------------------------------

    [Fact]
    public void ApplySetsEveryManagedValue()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "1");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();

        Assert.Equal("0", mouse.Peek("MouseSpeed"));
        Assert.Equal("0", mouse.Peek("MouseThreshold1"));
        Assert.Equal("0", mouse.Peek("MouseThreshold2"));
        Assert.Equal(TweakState.Applied, mouse.GetState());
    }

    [Fact]
    public void ApplyRecordsThePreviousValuesSoRevertRestoresThem()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "1");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();
        mouse.Revert();

        Assert.Equal("1", mouse.Peek("MouseSpeed"));
        Assert.Equal("6", mouse.Peek("MouseThreshold1"));
        Assert.Equal("10", mouse.Peek("MouseThreshold2"));
    }

    [Fact]
    public void ApplyingAnAlreadyAppliedValueRecordsNoBackupForIt()
    {
        // Recording "0" as the previous value would make revert restore the applied state, so the
        // tweak could never actually be undone.
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "0");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();
        mouse.Revert();

        // MouseSpeed falls back to the documented Windows default rather than to "0".
        Assert.Equal("1", mouse.Peek("MouseSpeed"));
        Assert.Equal("6", mouse.Peek("MouseThreshold1"));
    }

    // --- Revert ----------------------------------------------------------------------------------

    /// <summary>
    /// Unlike the network tweaks, revert here writes rather than deletes. These values are part of a
    /// default Windows profile, so removing them is not a state Windows itself produces - restoring
    /// the documented default is.
    /// </summary>
    [Fact]
    public void RevertWithNoBackupWritesTheDocumentedWindowsDefault()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "0");
        mouse.Seed("MouseThreshold1", "0");
        mouse.Seed("MouseThreshold2", "0");

        mouse.Revert();

        Assert.Equal("1", mouse.Peek("MouseSpeed"));
        Assert.Equal("6", mouse.Peek("MouseThreshold1"));
        Assert.Equal("10", mouse.Peek("MouseThreshold2"));
    }

    [Fact]
    public void RevertClearsTheBackupSoASecondRevertUsesTheDefault()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "3");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();
        mouse.Revert();
        Assert.Equal("3", mouse.Peek("MouseSpeed"));

        mouse.Revert();
        Assert.Equal("1", mouse.Peek("MouseSpeed"));
    }

    [Fact]
    public void ApplyRevertApplyRecordsTheBaselineFreshEachTime()
    {
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "2");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        mouse.Apply();
        mouse.Revert();
        Assert.Equal("2", mouse.Peek("MouseSpeed"));

        mouse.Seed("MouseSpeed", "3");
        mouse.Apply();
        mouse.Revert();

        Assert.Equal("3", mouse.Peek("MouseSpeed"));
    }

    // --- The keyboard tweak shares all of the above ----------------------------------------------

    [Fact]
    public void TheKeyboardTweakAppliesAndRevertsThroughTheSameSharedLogic()
    {
        var keyboard = new FakeKeyboard(_backupStore);
        keyboard.Seed("KeyboardDelay", "1");
        keyboard.Seed("KeyboardSpeed", "20");

        keyboard.Apply();
        Assert.Equal("0", keyboard.Peek("KeyboardDelay"));
        Assert.Equal("31", keyboard.Peek("KeyboardSpeed"));
        Assert.Equal(TweakState.Applied, keyboard.GetState());

        keyboard.Revert();
        Assert.Equal("1", keyboard.Peek("KeyboardDelay"));
        Assert.Equal("20", keyboard.Peek("KeyboardSpeed"));
    }

    [Fact]
    public void KeyboardSpeedHasTheSameAppliedValueAndDefaultBecause31IsAlreadyTheMaximum()
    {
        // Windows exposes no faster repeat rate, so this tweak really only changes the delay. The
        // value stays in the list so the state check confirms both halves rather than reporting
        // "fastest repeat rate" on a profile where something had lowered the speed.
        var keyboard = new FakeKeyboard(_backupStore);
        keyboard.Seed("KeyboardDelay", "0");
        keyboard.Seed("KeyboardSpeed", "10");

        Assert.Equal(TweakState.NotApplied, keyboard.GetState());

        keyboard.Revert();
        Assert.Equal("31", keyboard.Peek("KeyboardSpeed"));
    }

    [Fact]
    public void TheTwoTweaksUseSeparateBackupNamespaces()
    {
        // Both manage a value under "Control Panel\...", so a shared key would let one tweak's revert
        // consume the other's recorded value.
        var mouse = new FakeMouse(_backupStore);
        mouse.Seed("MouseSpeed", "1");
        mouse.Seed("MouseThreshold1", "6");
        mouse.Seed("MouseThreshold2", "10");

        var keyboard = new FakeKeyboard(_backupStore);
        keyboard.Seed("KeyboardDelay", "1");
        keyboard.Seed("KeyboardSpeed", "20");

        mouse.Apply();
        keyboard.Apply();

        Assert.NotNull(_backupStore.TryGet(@"registry:HKCU\Control Panel\Mouse\MouseSpeed"));
        Assert.NotNull(_backupStore.TryGet(@"registry:HKCU\Control Panel\Keyboard\KeyboardDelay"));

        mouse.Revert();

        // Reverting the mouse must leave the keyboard's backup untouched.
        Assert.NotNull(_backupStore.TryGet(@"registry:HKCU\Control Panel\Keyboard\KeyboardDelay"));
        keyboard.Revert();
        Assert.Equal("1", keyboard.Peek("KeyboardDelay"));
    }

    [Fact]
    public void BothTweaksKeepTheirOriginalIdsSoSavedProfilesStillResolve()
    {
        Assert.Equal("mouse.pointer-precision", new FakeMouse(_backupStore).Definition.Id);
        Assert.Equal("keyboard.repeat-rate", new FakeKeyboard(_backupStore).Definition.Id);
    }
}
