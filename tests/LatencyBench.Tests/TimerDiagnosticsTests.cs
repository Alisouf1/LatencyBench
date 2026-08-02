using System;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.Timers;

namespace LatencyBench.Tests;

public class TimerDiagnosticsTests
{
    /// <summary>Shaped exactly like real bcdedit output, including the alignment padding.</summary>
    private const string BootLoaderOutput = """
		Windows Boot Loader
		-------------------
		identifier              {current}
		device                  partition=C:
		path                    \WINDOWS\system32\winload.efi
		description             Windows 11
		locale                  en-US
		inherit                 {bootloadersettings}
		recoveryenabled         Yes
		allowedinmemorysettings 0x15000075
		osdevice                partition=C:
		systemroot              \WINDOWS
		nx                      OptIn
		bootmenupolicy          Standard
		useplatformclock        Yes
		disabledynamictick      No
		""";

    [Fact]
    public void ReadsAFlagThatIsOn()
    {
        Assert.Equal(BcdFlagState.On, TimerDiagnostics.ParseBootFlag(BootLoaderOutput, "useplatformclock"));
    }

    [Fact]
    public void ReadsAFlagThatIsOff()
    {
        Assert.Equal(BcdFlagState.Off, TimerDiagnostics.ParseBootFlag(BootLoaderOutput, "disabledynamictick"));
    }

    [Fact]
    public void AnAbsentFlagIsNotSetRatherThanOff()
    {
        // "Not present" is the Windows default and a different fact from "present and disabled" —
        // conflating them would make the platform clock rule silent on a machine that never had it.
        Assert.Equal(BcdFlagState.NotSet, TimerDiagnostics.ParseBootFlag(BootLoaderOutput, "useplatformtick"));
    }

    [Fact]
    public void DoesNotMatchAnElementNameThatIsMerelyAPrefix()
    {
        // "useplatformclock" must not be found by looking for "useplatform".
        const string output = "useplatformclock        Yes";

        Assert.Equal(BcdFlagState.NotSet, TimerDiagnostics.ParseBootFlag(output, "useplatform"));
    }

    [Fact]
    public void DoesNotMatchAnElementNameEmbeddedInAnotherValue()
    {
        const string output = """
			description             my useplatformclock backup entry
			bootmenupolicy          Standard
			""";

        Assert.Equal(BcdFlagState.NotSet, TimerDiagnostics.ParseBootFlag(output, "useplatformclock"));
    }

    [Theory]
    [InlineData("true", BcdFlagState.On)]
    [InlineData("on", BcdFlagState.On)]
    [InlineData("1", BcdFlagState.On)]
    [InlineData("YES", BcdFlagState.On)]
    [InlineData("false", BcdFlagState.Off)]
    [InlineData("off", BcdFlagState.Off)]
    [InlineData("0", BcdFlagState.Off)]
    public void AcceptsEveryFormBcdeditAndBcdeditSetUse(string value, BcdFlagState expected)
    {
        Assert.Equal(expected, TimerDiagnostics.ParseBootFlag($"useplatformclock   {value}", "useplatformclock"));
    }

    [Fact]
    public void ALocalisedBooleanIsReportedAsUnknownRatherThanGuessed()
    {
        // bcdedit never localises element names but does localise Yes/No. Reporting Unknown makes the
        // rule say "could not determine" instead of confidently claiming the flag is off.
        Assert.Equal(BcdFlagState.Unknown, TimerDiagnostics.ParseBootFlag("useplatformclock   Oui", "useplatformclock"));
    }

    [Fact]
    public void EmptyOutputYieldsNotSet()
    {
        Assert.Equal(BcdFlagState.NotSet, TimerDiagnostics.ParseBootFlag(string.Empty, "useplatformclock"));
    }

    // ---- Live timer resolution -------------------------------------------------------------

    [Fact]
    public void ReadsSaneTimerResolutionFromTheKernel()
    {
        var state = new TimerDiagnostics().Read(new WindowsInfoReader().Read().BuildNumber);

        // The finest supported interval is the smallest number, the coarsest is the largest — the
        // underlying API names these the opposite way round, which is easy to get backwards.
        Assert.True(state.FinestMs > 0, "The finest supported interval must be positive.");
        Assert.True(
            state.FinestMs <= state.CoarsestMs,
            $"Finest ({state.FinestMs} ms) should not exceed coarsest ({state.CoarsestMs} ms).");
        Assert.InRange(state.CurrentMs, state.FinestMs, state.CoarsestMs);

        // Every Windows machine sits in this range: 15.625 ms idle default down to 0.5 ms.
        Assert.InRange(state.CoarsestMs, 1.0, 100.0);
        Assert.InRange(state.FinestMs, 0.0001, 5.0);
    }

    [Fact]
    public void DetectsPerProcessTimerResolutionFromTheBuildNumber()
    {
        var diagnostics = new TimerDiagnostics();

        Assert.False(diagnostics.Read(TimerDiagnostics.PerProcessTimerResolutionBuild - 1).UsesPerProcessTimerResolution);
        Assert.True(diagnostics.Read(TimerDiagnostics.PerProcessTimerResolutionBuild).UsesPerProcessTimerResolution);
    }

    [Fact]
    public void ReportsRaisedTimerConsistentlyWithTheCurrentValue()
    {
        var state = new TimerDiagnostics().Read(26100);

        Assert.Equal(state.CurrentMs < state.CoarsestMs - 0.001, state.IsRaised);
    }

    [Fact]
    public void BootConfigurationFailureIsReportedRatherThanGuessedAsNotSet()
    {
        // The test host is not elevated, so bcdedit cannot read the store. What must not happen is the
        // flags coming back as NotSet, which would let the platform clock rule declare the machine
        // clean without having looked.
        var state = new TimerDiagnostics().Read(26100);

        if (state.BootConfigurationError is not null)
        {
            Assert.Equal(BcdFlagState.Unknown, state.UsePlatformClock);
            Assert.Equal(BcdFlagState.Unknown, state.UsePlatformTick);
            Assert.Equal(BcdFlagState.Unknown, state.DisableDynamicTick);
        }
    }
}
