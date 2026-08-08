using System;
using System.Collections.Generic;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Power;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the two powercfg-backed tweaks through the seams on PowerCfgRunner, so none of this
/// launches a process or changes the machine's power plan.
///
/// The behaviour that matters here is what gets recorded before a change and what happens when the
/// world has moved on since - a user's Ultimate Performance or OEM plan must survive being reverted,
/// and a plan that no longer exists must fail loudly rather than leaving them somewhere they did not
/// choose.
/// </summary>
public sealed class PowerTweakTests : IDisposable
{
    private const string HighPerformance = PowerCfgRunner.SchemeHighPerformance;
    private const string Balanced = PowerCfgRunner.SchemeBalanced;
    private const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private readonly string _directory;
    private readonly TweakBackupStore _backupStore;

    public PowerTweakTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchPower", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _backupStore = new TweakBackupStore(Path.Combine(_directory, "tweak-backups.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Models the machine's power configuration in memory.</summary>
    private sealed class FakePowerCfg : PowerCfgRunner
    {
        public FakePowerCfg(string activeScheme = Balanced) => ActiveScheme = activeScheme;

        public string ActiveScheme { get; private set; }

        public HashSet<string> ExistingSchemes { get; } =
            new(new[] { Balanced, HighPerformance }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Set to simulate an OEM utility or policy that immediately reverts the change.</summary>
        public bool IgnoreActivation { get; set; }

        public List<string> Activations { get; } = new();

        public override string GetActiveSchemeGuid() => ActiveScheme;

        public override bool SchemeExists(string schemeGuid) => ExistingSchemes.Contains(schemeGuid);

        public override (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
        {
            if (arguments.Length >= 2 && arguments[0] == "/setactive")
            {
                Activations.Add(arguments[1]);
                if (!IgnoreActivation)
                {
                    ActiveScheme = arguments[1];
                }
            }

            return (0, string.Empty, string.Empty);
        }
    }

    private HighPerformancePlanTweak Tweak(PowerCfgRunner powerCfg) => new(powerCfg, _backupStore);

    // --- State -----------------------------------------------------------------------------------

    [Fact]
    public void StateIsAppliedWhenHighPerformanceIsActive()
    {
        Assert.Equal(TweakState.Applied, Tweak(new FakePowerCfg(HighPerformance)).GetState());
    }

    [Fact]
    public void StateIsNotAppliedWhenAnotherPlanIsActive()
    {
        Assert.Equal(TweakState.NotApplied, Tweak(new FakePowerCfg(Balanced)).GetState());
    }

    [Fact]
    public void StateComparisonIgnoresCase()
    {
        // The registry holds whatever casing Windows wrote; a case-sensitive check would report a
        // correctly-applied tweak as not applied.
        Assert.Equal(TweakState.Applied, Tweak(new FakePowerCfg(HighPerformance.ToUpperInvariant())).GetState());
    }

    // --- Apply -----------------------------------------------------------------------------------

    [Fact]
    public void ApplyActivatesHighPerformance()
    {
        var powerCfg = new FakePowerCfg(Balanced);

        Tweak(powerCfg).Apply();

        Assert.Equal(HighPerformance, powerCfg.ActiveScheme);
    }

    /// <summary>
    /// The behaviour that protects a user's deliberate choice. Reverting to a hardcoded Balanced
    /// would destroy an Ultimate Performance or OEM plan they had selected on purpose, so what was
    /// actually active is what gets recorded.
    /// </summary>
    [Fact]
    public void ApplyRecordsThePlanThatWasReallyActiveNotAnAssumedDefault()
    {
        var powerCfg = new FakePowerCfg(UltimatePerformance);
        powerCfg.ExistingSchemes.Add(UltimatePerformance);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(UltimatePerformance, powerCfg.ActiveScheme);
    }

    [Fact]
    public void ApplyingWhenHighPerformanceIsAlreadyActiveRecordsNoBackup()
    {
        // Recording High performance as the "previous" plan would make a later revert a no-op that
        // looks like it worked.
        var powerCfg = new FakePowerCfg(HighPerformance);

        var tweak = Tweak(powerCfg);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(HighPerformance, powerCfg.ActiveScheme);
    }

    [Fact]
    public void ApplyExplainsItselfWhenWindowsDoesNotExposeThePlan()
    {
        // Windows 11 hides High performance on many modern-standby systems. An opaque powercfg exit
        // code here would be indistinguishable from a bug in this app.
        var powerCfg = new FakePowerCfg(Balanced);
        powerCfg.ExistingSchemes.Remove(HighPerformance);

        var ex = Assert.Throws<InvalidOperationException>(() => Tweak(powerCfg).Apply());

        Assert.Contains("does not expose the High performance power plan", ex.Message, StringComparison.Ordinal);
        Assert.Empty(powerCfg.Activations);
    }

    /// <summary>
    /// powercfg reporting success is not proof the plan changed. An OEM power utility or a group
    /// policy can put it straight back, and reporting that as applied would be a false measurement.
    /// </summary>
    [Fact]
    public void ApplyFailsWhenSomethingElseImmediatelyRevertsThePlan()
    {
        var powerCfg = new FakePowerCfg(Balanced) { IgnoreActivation = true };

        var ex = Assert.Throws<InvalidOperationException>(() => Tweak(powerCfg).Apply());

        Assert.Contains("reported success but the active power plan is still not", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overriding it", ex.Message, StringComparison.Ordinal);
    }

    // --- Revert ----------------------------------------------------------------------------------

    [Fact]
    public void RevertWithNoRecordedBackupChangesNothing()
    {
        // High performance was already active before LatencyBench touched anything, so switching to
        // an assumed default would be a change the user never asked for.
        var powerCfg = new FakePowerCfg(HighPerformance);

        Tweak(powerCfg).Revert();

        Assert.Empty(powerCfg.Activations);
        Assert.Equal(HighPerformance, powerCfg.ActiveScheme);
    }

    [Fact]
    public void RevertRestoresTheRecordedPlan()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        var tweak = Tweak(powerCfg);

        tweak.Apply();
        Assert.Equal(HighPerformance, powerCfg.ActiveScheme);

        tweak.Revert();
        Assert.Equal(Balanced, powerCfg.ActiveScheme);
    }

    [Fact]
    public void RevertFailsLoudlyWhenTheRecordedPlanNoLongerExists()
    {
        // A plan can be deleted between apply and revert. Silently substituting another would leave
        // the user somewhere they never chose.
        var powerCfg = new FakePowerCfg(UltimatePerformance);
        powerCfg.ExistingSchemes.Add(UltimatePerformance);

        var tweak = Tweak(powerCfg);
        tweak.Apply();

        powerCfg.ExistingSchemes.Remove(UltimatePerformance);

        var ex = Assert.Throws<InvalidOperationException>(() => tweak.Revert());
        Assert.Contains("no longer exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevertClearsTheBackupSoASecondRevertIsANoOp()
    {
        var powerCfg = new FakePowerCfg(Balanced);
        var tweak = Tweak(powerCfg);

        tweak.Apply();
        tweak.Revert();
        powerCfg.Activations.Clear();

        tweak.Revert();

        Assert.Empty(powerCfg.Activations);
    }

    [Fact]
    public void ApplyRevertApplyRecordsTheBaselineFreshEachTime()
    {
        // Revert removes the backup, so the next Apply records whatever is active then - not a stale
        // value from before the previous revert.
        var powerCfg = new FakePowerCfg(Balanced);
        var tweak = Tweak(powerCfg);

        tweak.Apply();
        tweak.Revert();
        Assert.Equal(Balanced, powerCfg.ActiveScheme);

        powerCfg.ExistingSchemes.Add(UltimatePerformance);
        powerCfg.Run("/setactive", UltimatePerformance);

        tweak.Apply();
        tweak.Revert();

        Assert.Equal(UltimatePerformance, powerCfg.ActiveScheme);
    }

    // --- Robustness ------------------------------------------------------------------------------

    [Fact]
    public void StateIsUnknownRatherThanGuessedWhenTheActiveSchemeCannotBeRead()
    {
        // Unelevated, or with a damaged power configuration. Reporting NotApplied would send the user
        // to apply something that may already be set.
        Assert.Equal(TweakState.Unknown, Tweak(new ThrowingPowerCfg()).GetState());
    }

    private sealed class ThrowingPowerCfg : PowerCfgRunner
    {
        public override string GetActiveSchemeGuid() =>
            throw new InvalidOperationException("Could not read the active power scheme.");
    }
}
