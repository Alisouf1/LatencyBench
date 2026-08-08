using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Services;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the SysMain tweak through its seams, so nothing here stops a real service or writes to the
/// machine's service configuration.
///
/// The behaviour that matters is what happens on revert. Getting it wrong means turning a service
/// back on that the user, or their machine image, had deliberately switched off - a change they never
/// asked for and would have no reason to look for.
/// </summary>
public sealed class SysMainServiceTweakTests : IDisposable
{
    private const int Delayed = 1;
    private const int Automatic = 2;
    private const int Manual = 3;
    private const int Disabled = 4;

    private readonly string _directory;
    private readonly TweakBackupStore _backupStore;

    public SysMainServiceTweakTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchSysMain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _backupStore = new TweakBackupStore(Path.Combine(_directory, "tweak-backups.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Models the service control manager and the service's Start value in memory.</summary>
    private sealed class FakeSysMain : SysMainServiceTweak
    {
        public FakeSysMain(
            TweakBackupStore backupStore,
            int startValue = Automatic,
            ServiceControllerStatus status = ServiceControllerStatus.Running)
            : base(backupStore)
        {
            StartValue = startValue;
            Status = status;
        }

        public int StartValue { get; private set; }

        public ServiceControllerStatus Status { get; private set; }

        public List<string> Actions { get; } = new();

        /// <summary>Set to simulate an edition that does not ship SysMain, or a denied query.</summary>
        public Exception? ReadStateThrows { get; set; }

        protected override (ServiceStartMode StartType, ServiceControllerStatus Status) ReadServiceState()
        {
            if (ReadStateThrows is not null)
            {
                throw ReadStateThrows;
            }

            ServiceStartMode mode = StartValue switch
            {
                Delayed or Automatic => ServiceStartMode.Automatic,
                Manual => ServiceStartMode.Manual,
                _ => ServiceStartMode.Disabled,
            };

            return (mode, Status);
        }

        protected override int ReadStartValue() => StartValue;

        protected override void WriteStartValue(int startValue)
        {
            Actions.Add($"write:{startValue}");
            StartValue = startValue;
        }

        protected override void StopServiceIfRunning()
        {
            Actions.Add("stop");
            Status = ServiceControllerStatus.Stopped;
        }

        protected override void StartServiceIfStopped()
        {
            Actions.Add("start");
            Status = ServiceControllerStatus.Running;
        }
    }

    // --- State -----------------------------------------------------------------------------------

    [Fact]
    public void StateIsAppliedOnlyWhenDisabledAndStopped()
    {
        var tweak = new FakeSysMain(_backupStore, Disabled, ServiceControllerStatus.Stopped);

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void StateIsNotAppliedWhenDisabledButStillRunning()
    {
        // Disabled takes effect at next boot; the service can still be running now. Reporting Applied
        // here would claim the background activity had stopped when it has not.
        var tweak = new FakeSysMain(_backupStore, Disabled, ServiceControllerStatus.Running);

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Theory]
    [InlineData(Automatic)]
    [InlineData(Delayed)]
    [InlineData(Manual)]
    public void StateIsNotAppliedForAnyStartModeOtherThanDisabled(int startValue)
    {
        var tweak = new FakeSysMain(_backupStore, startValue, ServiceControllerStatus.Stopped);

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void StateIsUnknownWhenTheServiceDoesNotExist()
    {
        // Some Windows editions and LTSC images do not ship SysMain. Reporting NotApplied would offer
        // the user a tweak that cannot work.
        var tweak = new FakeSysMain(_backupStore)
        {
            ReadStateThrows = new InvalidOperationException("Service SysMain was not found on computer."),
        };

        Assert.Equal(TweakState.Unknown, tweak.GetState());
    }

    [Fact]
    public void StateIsUnknownWhenQueryingTheServiceIsDenied()
    {
        var tweak = new FakeSysMain(_backupStore) { ReadStateThrows = new Win32Exception(5) };

        Assert.Equal(TweakState.Unknown, tweak.GetState());
    }

    [Fact]
    public void AnUnexpectedExceptionIsNotSwallowedAsUnknown()
    {
        // Only "no such service" and "access denied" are known-benign. Anything else is a real fault
        // and must surface rather than being reported as an unreadable state.
        var tweak = new FakeSysMain(_backupStore) { ReadStateThrows = new OutOfMemoryException() };

        Assert.Throws<OutOfMemoryException>(() => tweak.GetState());
    }

    // --- Apply -----------------------------------------------------------------------------------

    [Fact]
    public void ApplyStopsTheServiceBeforeDisablingIt()
    {
        // Writing Disabled first would leave it running until reboot with no way for the stop to be
        // attributed to this tweak.
        var tweak = new FakeSysMain(_backupStore, Automatic, ServiceControllerStatus.Running);

        tweak.Apply();

        Assert.Equal(new[] { "stop", $"write:{Disabled}" }, tweak.Actions);
        Assert.Equal(Disabled, tweak.StartValue);
    }

    /// <summary>
    /// The regression this suite exists for. Revert used to assume Automatic unconditionally, so on a
    /// machine where SysMain had been set to Manual - or disabled by the user or an image policy -
    /// reverting silently turned the service back on against their wishes.
    /// </summary>
    [Theory]
    [InlineData(Manual)]
    [InlineData(Delayed)]
    [InlineData(Automatic)]
    public void ApplyRecordsTheStartModeThatWasReallySetAndRevertRestoresIt(int original)
    {
        var tweak = new FakeSysMain(_backupStore, original, ServiceControllerStatus.Running);

        tweak.Apply();
        Assert.Equal(Disabled, tweak.StartValue);

        tweak.Revert();
        Assert.Equal(original, tweak.StartValue);
    }

    // --- Revert ----------------------------------------------------------------------------------

    [Fact]
    public void RevertingToManualRestoresTheModeWithoutStartingTheService()
    {
        // Manual means "available on demand", not "running now". Starting it would be a change the
        // user never had.
        var tweak = new FakeSysMain(_backupStore, Manual, ServiceControllerStatus.Running);

        tweak.Apply();
        tweak.Actions.Clear();
        tweak.Revert();

        Assert.Equal(new[] { $"write:{Manual}" }, tweak.Actions);
        Assert.DoesNotContain("start", tweak.Actions);
    }

    [Theory]
    [InlineData(Automatic)]
    [InlineData(Delayed)]
    public void RevertingToAStartOnBootModeStartsTheServiceAgain(int original)
    {
        var tweak = new FakeSysMain(_backupStore, original, ServiceControllerStatus.Running);

        tweak.Apply();
        tweak.Actions.Clear();
        tweak.Revert();

        Assert.Equal(new[] { $"write:{original}", "start" }, tweak.Actions);
        Assert.Equal(ServiceControllerStatus.Running, tweak.Status);
    }

    [Fact]
    public void RevertWithNoRecordedBackupChangesNothing()
    {
        // SysMain was already disabled before LatencyBench touched it. Enabling it now would be a
        // change the user never asked for.
        var tweak = new FakeSysMain(_backupStore, Disabled, ServiceControllerStatus.Stopped);

        tweak.Revert();

        Assert.Empty(tweak.Actions);
        Assert.Equal(Disabled, tweak.StartValue);
    }

    [Fact]
    public void ACorruptBackupFailsLoudlyRatherThanGuessingAStartMode()
    {
        _backupStore.Save("service:SysMain\\Start", "not-a-number");
        var tweak = new FakeSysMain(_backupStore, Disabled, ServiceControllerStatus.Stopped);

        var ex = Assert.Throws<InvalidOperationException>(() => tweak.Revert());

        Assert.Contains("backup", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(tweak.Actions);
    }

    [Fact]
    public void RevertClearsTheBackupSoASecondRevertIsANoOp()
    {
        var tweak = new FakeSysMain(_backupStore, Automatic, ServiceControllerStatus.Running);

        tweak.Apply();
        tweak.Revert();
        tweak.Actions.Clear();

        tweak.Revert();

        Assert.Empty(tweak.Actions);
    }

    [Fact]
    public void ApplyRevertApplyRecordsTheBaselineFreshEachTime()
    {
        // Revert removes the backup, so a second Apply records whatever is set then rather than a
        // stale value from before the previous revert.
        var tweak = new FakeSysMain(_backupStore, Automatic, ServiceControllerStatus.Running);

        tweak.Apply();
        tweak.Revert();
        Assert.Equal(Automatic, tweak.StartValue);

        // The user switches it to Manual themselves, then applies again.
        var second = new FakeSysMain(_backupStore, Manual, ServiceControllerStatus.Running);
        second.Apply();
        second.Revert();

        Assert.Equal(Manual, second.StartValue);
    }

    [Fact]
    public void TheTweakIsMarkedAggressiveSoProfilesCanDeclineIt()
    {
        // Disabling SysMain is a real trade - slower first launches of large applications - so it must
        // not be swept up by a profile that only takes no-downside changes.
        var tweak = new FakeSysMain(_backupStore);

        Assert.Equal(TweakRisk.Aggressive, tweak.Definition.Risk);
        Assert.Equal(TweakCategory.Aggressive, tweak.Definition.Category);
    }
}
