using System;
using System.Collections.Generic;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the generic powercfg AC-value tweak - the one behind minimum processor state, USB selective
/// suspend, core parking and boost mode. All of it runs through overridden seams, so nothing here
/// launches powercfg or changes the machine's power configuration.
///
/// The behaviour that matters is that a value is recorded against the scheme it was read from. Power
/// settings are per-scheme, so a backup that does not remember which scheme it came from restores
/// the wrong plan's value.
/// </summary>
public sealed class PowerCfgAcValueTweakTests : IDisposable
{
    private const string Balanced = PowerCfgRunner.SchemeBalanced;
    private const string HighPerformance = PowerCfgRunner.SchemeHighPerformance;
    private const string Subgroup = PowerCfgRunner.SubgroupProcessor;
    private const string Setting = PowerCfgRunner.SettingProcessorMinState;

    private readonly string _directory;
    private readonly TweakBackupStore _backupStore;

    public PowerCfgAcValueTweakTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchAcValue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _backupStore = new TweakBackupStore(Path.Combine(_directory, "tweak-backups.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Holds per-scheme setting values in memory.</summary>
    private sealed class FakePowerCfg : PowerCfgRunner
    {
        private readonly Dictionary<string, uint> _values = new(StringComparer.OrdinalIgnoreCase);

        public FakePowerCfg(string activeScheme = Balanced) => ActiveScheme = activeScheme;

        public string ActiveScheme { get; set; }

        /// <summary>Settings with no stored override at all - the case that has no value to read back.</summary>
        public HashSet<string> Unset { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Simulates a scheme that silently refuses the write.</summary>
        public bool RejectWrites { get; set; }

        public List<string> Writes { get; } = new();

        private static string Key(string scheme, string setting) => $"{scheme}|{setting}";

        public void Seed(string scheme, string setting, uint value) => _values[Key(scheme, setting)] = value;

        public override string GetActiveSchemeGuid() => ActiveScheme;

        public override uint? QueryAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid)
        {
            if (Unset.Contains(Key(schemeGuid, settingGuid)))
            {
                return null;
            }

            return _values.TryGetValue(Key(schemeGuid, settingGuid), out uint value) ? value : null;
        }

        public override void SetAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid, uint value)
        {
            Writes.Add($"{schemeGuid}|{settingGuid}={value}");
            if (!RejectWrites)
            {
                _values[Key(schemeGuid, settingGuid)] = value;
                Unset.Remove(Key(schemeGuid, settingGuid));
            }
        }
    }

    private PowerCfgAcValueTweak Tweak(PowerCfgRunner powerCfg, uint applied = 100, uint fallbackDefault = 5) =>
        new(
            new TweakDefinition
            {
                Id = "cpu.min-processor-state",
                Category = TweakCategory.Power,
                Name = "Minimum processor state 100%",
                Description = "test",
                Risk = TweakRisk.Safe,
            },
            powerCfg, Subgroup, Setting, applied, fallbackDefault, _backupStore);

    // --- State -----------------------------------------------------------------------------------

    [Fact]
    public void StateIsAppliedWhenTheActiveSchemeHoldsTheAppliedValue()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 100);

        Assert.Equal(TweakState.Applied, Tweak(powerCfg).GetState());
    }

    [Fact]
    public void StateIsNotAppliedWhenTheActiveSchemeHoldsSomethingElse()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 5);

        Assert.Equal(TweakState.NotApplied, Tweak(powerCfg).GetState());
    }

    [Fact]
    public void StateIsUnknownWhenTheSettingIsNotExposedOnThisSystem()
    {
        // Not every processor setting exists on every machine. Reporting NotApplied would offer a
        // tweak that cannot work.
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Unset.Add($"{Balanced}|{Setting}");

        Assert.Equal(TweakState.Unknown, Tweak(powerCfg).GetState());
    }

    [Fact]
    public void StateIsReadFromWhicheverSchemeIsActive()
    {
        // Power settings are per-scheme. Reading a hardcoded scheme would report the wrong state
        // whenever the user is not on it.
        var powerCfg = new FakePowerCfg(HighPerformance);
        powerCfg.Seed(Balanced, Setting, 5);
        powerCfg.Seed(HighPerformance, Setting, 100);

        Assert.Equal(TweakState.Applied, Tweak(powerCfg).GetState());
    }

    // --- Apply -----------------------------------------------------------------------------------

    [Fact]
    public void ApplyWritesTheValueToTheActiveScheme()
    {
        var powerCfg = new FakePowerCfg(HighPerformance);
        powerCfg.Seed(HighPerformance, Setting, 5);

        Tweak(powerCfg).Apply();

        Assert.Equal(new[] { $"{HighPerformance}|{Setting}=100" }, powerCfg.Writes);
    }

    [Fact]
    public void ApplyRecordsThePreviousValueSoRevertCanRestoreIt()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 42);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(42u, powerCfg.QueryAcValueIndex(Balanced, Subgroup, Setting));
    }

    /// <summary>
    /// When a setting has never been overridden on a scheme there is no stored value to read back.
    /// Without the documented Windows default as a fallback the tweak would apply with no backup
    /// recorded at all, and Revert would become a silent no-op that looks like it worked.
    /// </summary>
    [Fact]
    public void ApplyFallsBackToTheDocumentedDefaultWhenNothingIsStored()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Unset.Add($"{Balanced}|{Setting}");

        var tweak = Tweak(powerCfg, applied: 100, fallbackDefault: 5);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(5u, powerCfg.QueryAcValueIndex(Balanced, Subgroup, Setting));
    }

    [Fact]
    public void ApplyingWhenTheValueIsAlreadySetRecordsNoBackup()
    {
        // Recording the applied value as the "previous" one would make a later revert restore the
        // very thing it was meant to undo.
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 100);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        powerCfg.Writes.Clear();

        tweak.Revert();

        Assert.Empty(powerCfg.Writes);
    }

    [Fact]
    public void ApplyFailsWhenWindowsDoesNotAcceptTheChange()
    {
        // powercfg can report success while the scheme refuses the setting. Verifying the read-back
        // is what turns that into an honest failure instead of a false "applied".
        var powerCfg = new FakePowerCfg(Balanced) { RejectWrites = true };
        powerCfg.Seed(Balanced, Setting, 5);

        var ex = Assert.Throws<InvalidOperationException>(() => Tweak(powerCfg).Apply());

        Assert.Contains("didn't accept the change", ex.Message, StringComparison.Ordinal);
    }

    // --- Revert ----------------------------------------------------------------------------------

    [Fact]
    public void RevertWithNoRecordedSchemeChangesNothing()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 100);

        Tweak(powerCfg).Revert();

        Assert.Empty(powerCfg.Writes);
    }

    /// <summary>
    /// Power settings are per-scheme, so the backup has to remember which scheme it was taken from.
    /// Restoring into whatever happens to be active later would write the wrong plan's value.
    /// </summary>
    [Fact]
    public void RevertRestoresIntoTheSchemeTheValueWasTakenFromEvenIfTheActiveSchemeChanged()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 42);
        powerCfg.Seed(HighPerformance, Setting, 7);

        var tweak = Tweak(powerCfg);
        tweak.Apply();

        // The user switches plans between applying and reverting.
        powerCfg.ActiveScheme = HighPerformance;
        tweak.Revert();

        Assert.Equal(42u, powerCfg.QueryAcValueIndex(Balanced, Subgroup, Setting));
        Assert.Equal(7u, powerCfg.QueryAcValueIndex(HighPerformance, Subgroup, Setting));
    }

    [Fact]
    public void ACorruptBackupFailsLoudlyRatherThanGuessingAValue()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 5);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        _backupStore.Save($"powercfg:{Balanced}\\{Subgroup}\\{Setting}", "not-a-number");

        Assert.Throws<InvalidOperationException>(() => tweak.Revert());
    }

    [Fact]
    public void RevertClearsBothBackupEntriesSoASecondRevertIsANoOp()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 42);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        tweak.Revert();
        powerCfg.Writes.Clear();

        tweak.Revert();

        Assert.Empty(powerCfg.Writes);
    }

    [Fact]
    public void ApplyRevertApplyRecordsTheBaselineFreshEachTime()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.Seed(Balanced, Setting, 42);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        tweak.Revert();
        Assert.Equal(42u, powerCfg.QueryAcValueIndex(Balanced, Subgroup, Setting));

        // The user sets it to something else themselves, then applies again.
        powerCfg.Seed(Balanced, Setting, 77);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(77u, powerCfg.QueryAcValueIndex(Balanced, Subgroup, Setting));
    }
}
