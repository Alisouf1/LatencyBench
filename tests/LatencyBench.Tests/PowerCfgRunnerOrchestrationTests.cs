using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the orchestration on top of powercfg.exe and the registry: which scheme gets edited, when a
/// write has to be followed by a re-activation, and how a failure is surfaced. The three members that
/// reach outside the process are overridden, so none of this launches a process or touches the
/// machine's power configuration.
/// </summary>
public sealed class PowerCfgRunnerOrchestrationTests
{
    private const string Balanced = PowerCfgRunner.SchemeBalanced;
    private const string HighPerformance = PowerCfgRunner.SchemeHighPerformance;
    private const string Subgroup = PowerCfgRunner.SubgroupProcessor;
    private const string Setting = PowerCfgRunner.SettingProcessorMinState;

    /// <summary>Records every powercfg invocation instead of making one.</summary>
    private sealed class FakeRunner : PowerCfgRunner
    {
        private readonly string _activeScheme;

        public FakeRunner(string activeScheme = Balanced) => _activeScheme = activeScheme;

        public List<string[]> Invocations { get; } = new();

        /// <summary>Exit code returned for a given first argument, e.g. "/setacvalueindex".</summary>
        public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.Ordinal);

        public string StdErr { get; set; } = string.Empty;

        public HashSet<string> ExistingSchemes { get; } =
            new(new[] { Balanced, HighPerformance }, StringComparer.OrdinalIgnoreCase);

        public int ActiveSchemeReads { get; private set; }

        public override (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
        {
            Invocations.Add(arguments);
            int exitCode = ExitCodes.TryGetValue(arguments[0], out int code) ? code : 0;
            return (exitCode, string.Empty, StdErr);
        }

        public override string GetActiveSchemeGuid()
        {
            ActiveSchemeReads++;
            return _activeScheme;
        }

        public override bool SchemeExists(string schemeGuid) => ExistingSchemes.Contains(schemeGuid);
    }

    private static string[] Args(FakeRunner runner, int index) => runner.Invocations[index];

    // --- SetActiveScheme -------------------------------------------------------------------------

    [Fact]
    public void SetActiveSchemePassesTheSchemeToPowercfg()
    {
        var runner = new FakeRunner();

        runner.SetActiveScheme(HighPerformance);

        var call = Assert.Single(runner.Invocations);
        Assert.Equal(new[] { "/setactive", HighPerformance }, call);
    }

    [Fact]
    public void SetActiveSchemeThrowsWithPowercfgsOwnMessageOnFailure()
    {
        // The user needs to know what Windows said, not just that something failed.
        var runner = new FakeRunner { StdErr = "The specified power scheme does not exist." };
        runner.ExitCodes["/setactive"] = 1;

        var ex = Assert.Throws<InvalidOperationException>(() => runner.SetActiveScheme(HighPerformance));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains(HighPerformance, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetActiveSchemeDoesNotSwallowAFailureAsSuccess()
    {
        var runner = new FakeRunner();
        runner.ExitCodes["/setactive"] = 5;

        Assert.Throws<InvalidOperationException>(() => runner.SetActiveScheme(Balanced));
    }

    // --- SetAcValueIndex -------------------------------------------------------------------------

    [Fact]
    public void SetAcValueIndexPassesEveryGuidAndTheValue()
    {
        var runner = new FakeRunner(activeScheme: HighPerformance);

        runner.SetAcValueIndex(Balanced, Subgroup, Setting, 100);

        Assert.Equal(
            new[] { "/setacvalueindex", Balanced, Subgroup, Setting, "100" },
            Args(runner, 0));
    }

    /// <summary>
    /// The regression this suite exists for. /setacvalueindex only edits the scheme's stored value;
    /// Windows does not push it into the running power policy until the scheme is activated. A tweak
    /// applied to the CURRENTLY ACTIVE scheme therefore read back as changed while having no effect
    /// on the machine at all - which is the worst possible outcome for a tool whose whole job is
    /// proving a change took.
    /// </summary>
    [Fact]
    public void EditingTheActiveSchemeReactivatesItSoTheChangeTakesEffect()
    {
        var runner = new FakeRunner(activeScheme: Balanced);

        runner.SetAcValueIndex(Balanced, Subgroup, Setting, 100);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal("/setacvalueindex", Args(runner, 0)[0]);
        Assert.Equal(new[] { "/setactive", Balanced }, Args(runner, 1));
    }

    [Fact]
    public void EditingAnInactiveSchemeDoesNotActivateIt()
    {
        // Re-activating here would silently switch the user's power plan as a side effect of editing
        // a different one.
        var runner = new FakeRunner(activeScheme: HighPerformance);

        runner.SetAcValueIndex(Balanced, Subgroup, Setting, 100);

        var call = Assert.Single(runner.Invocations);
        Assert.Equal("/setacvalueindex", call[0]);
    }

    [Fact]
    public void TheActiveSchemeComparisonIgnoresCase()
    {
        // Windows reports the active scheme in whatever casing the registry holds; a case-sensitive
        // comparison would skip the re-activation and reintroduce the silent no-op.
        var runner = new FakeRunner(activeScheme: Balanced.ToUpperInvariant());

        runner.SetAcValueIndex(Balanced.ToLowerInvariant(), Subgroup, Setting, 100);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal("/setactive", Args(runner, 1)[0]);
    }

    [Fact]
    public void AFailedWriteThrowsBeforeAnyReactivationIsAttempted()
    {
        // Re-activating after a failed edit would apply nothing while looking like it worked.
        var runner = new FakeRunner(activeScheme: Balanced);
        runner.ExitCodes["/setacvalueindex"] = 1;
        runner.StdErr = "Access is denied.";

        var ex = Assert.Throws<InvalidOperationException>(
            () => runner.SetAcValueIndex(Balanced, Subgroup, Setting, 100));

        Assert.Contains("Access is denied", ex.Message, StringComparison.Ordinal);
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("/setacvalueindex", call[0]);
        Assert.Equal(0, runner.ActiveSchemeReads);
    }

    [Fact]
    public void AFailedReactivationSurfacesRatherThanBeingSwallowed()
    {
        // The value was written but is not live. Reporting success here would be a lie.
        var runner = new FakeRunner(activeScheme: Balanced);
        runner.ExitCodes["/setactive"] = 1;
        runner.StdErr = "The service cannot be started.";

        Assert.Throws<InvalidOperationException>(
            () => runner.SetAcValueIndex(Balanced, Subgroup, Setting, 100));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(100u)]
    [InlineData(uint.MaxValue)]
    public void TheValueIsPassedAsAPlainInvariantNumber(uint value)
    {
        // Formatted with the invariant culture, or a locale using a different digit or group
        // separator would hand powercfg something it cannot parse.
        var runner = new FakeRunner(activeScheme: HighPerformance);

        runner.SetAcValueIndex(Balanced, Subgroup, Setting, value);

        string formatted = Args(runner, 0)[4];
        Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), formatted);
        Assert.DoesNotContain(",", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(".", formatted, StringComparison.Ordinal);
    }

    // --- Argument safety -------------------------------------------------------------------------

    [Fact]
    public void EachArgumentIsPassedAsItsOwnTokenRatherThanOneConcatenatedString()
    {
        // ArgumentList hands each token to CreateProcess separately, so there is no shell-style
        // re-parsing step for a value to break out of. Collapsing these into one string would
        // reintroduce that class of problem the moment a caller passes something less trusted than
        // today's hardcoded GUIDs.
        var runner = new FakeRunner(activeScheme: HighPerformance);

        runner.SetAcValueIndex(Balanced, Subgroup, Setting, 50);

        Assert.Equal(5, Args(runner, 0).Length);
        Assert.All(Args(runner, 0), token => Assert.DoesNotContain(" ", token, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationRunsBeforeAnyProcessIsLaunched()
    {
        // A malformed GUID must be refused without spending a process launch on it.
        var runner = new FakeRunner();

        Assert.Throws<ArgumentException>(() => runner.SetAcValueIndex("bad", Subgroup, Setting, 1));

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void ValidationRunsBeforeTheActiveSchemeIsEvenRead()
    {
        var runner = new FakeRunner();

        Assert.Throws<ArgumentException>(() => runner.SetActiveScheme("not-a-guid"));

        Assert.Equal(0, runner.ActiveSchemeReads);
        Assert.Empty(runner.Invocations);
    }
}
