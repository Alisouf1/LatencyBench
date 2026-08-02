using System;
using System.Collections.Generic;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.DevicePower;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises <see cref="DevicePowerManagementTweak"/> against a fake device-power read/write pair
/// rather than the real WMI (MSPower_DeviceEnable) calls, so Apply/Revert can be asserted without
/// touching any real device.
/// <para>
/// Two regressions this suite exists for: Apply() used to silently skip the backup and write anyway
/// when a device's current state couldn't be read (<see cref="ApplyThrowsWhenADevicesStateCannotBeDetermined"/>
/// fails against the old "proceed anyway" behavior); and Revert() used to treat "no backup" as
/// meaning "turn power management back on", which doesn't distinguish a device that was already off
/// before Apply ran from one whose read genuinely failed
/// (<see cref="RevertDoesNotEnablePowerManagementForADeviceThatWasAlreadyOff"/> fails against the old
/// default-to-true behavior).
/// </para>
/// </summary>
public sealed class DevicePowerManagementTweakTests : IDisposable
{
    private readonly string _backupDirectory;
    private readonly string _backupFilePath;

    public DevicePowerManagementTweakTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupDirectory);
        _backupFilePath = Path.Combine(_backupDirectory, "tweak-backups.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_backupDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private DevicePowerManagementTweak CreateTweak(FakeDevicePower fake, TweakBackupStore? backupStore = null) =>
        new(
            new TweakDefinition
            {
                Id = "test.device-power",
                Category = TweakCategory.Usb,
                Name = "Test device power tweak",
                Description = "A throwaway tweak definition for DevicePowerManagementTweak tests.",
            },
            fake.InstanceIds,
            backupStore ?? new TweakBackupStore(_backupFilePath),
            fake.Read,
            fake.Write);

    [Fact]
    public void ApplyDisablesPowerManagementForEveryReadableDevice()
    {
        var fake = new FakeDevicePower(("device1", true), ("device2", true));
        var tweak = CreateTweak(fake);

        tweak.Apply();

        Assert.Equal(false, fake.Read("device1"));
        Assert.Equal(false, fake.Read("device2"));
        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void ApplyThrowsWhenADevicesStateCannotBeDetermined()
    {
        // Regression: the old code silently treated an unreadable device the same as "already off"
        // and wrote false anyway, discarding any chance of knowing whether it was actually on.
        var fake = new FakeDevicePower(("device1", true), ("device2", null));
        var tweak = CreateTweak(fake);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
    }

    [Fact]
    public void ApplyLeavesEarlierDevicesCorrectlyAppliedWhenALaterDeviceThrows()
    {
        // Fail-fast rather than all-or-nothing: whatever was already processed before the unreadable
        // device is hit stays correctly applied and backed up, so Revert can still undo exactly that.
        var fake = new FakeDevicePower(("device1", true), ("device2", null));
        var tweak = CreateTweak(fake);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());

        Assert.Equal(false, fake.Read("device1"));
    }

    [Fact]
    public void RevertRestoresPowerManagementForADeviceThatWasOnBeforeApply()
    {
        var fake = new FakeDevicePower(("device1", true));
        var tweak = CreateTweak(fake);
        tweak.Apply();

        tweak.Revert();

        Assert.Equal(true, fake.Read("device1"));
        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void RevertDoesNotEnablePowerManagementForADeviceThatWasAlreadyOff()
    {
        // The core regression: no backup must mean "leave it off", not "turn it on".
        var fake = new FakeDevicePower(("device1", false));
        var tweak = CreateTweak(fake);
        tweak.Apply(); // Already off — no backup should be recorded.

        tweak.Revert();

        Assert.Equal(false, fake.Read("device1"));
    }

    [Fact]
    public void RevertRemovesTheBackupEntrySoASecondRevertIsANoOp()
    {
        var fake = new FakeDevicePower(("device1", true));
        var backupStore = new TweakBackupStore(_backupFilePath);
        var tweak = CreateTweak(fake, backupStore);
        tweak.Apply();

        tweak.Revert();
        fake.Write("device1", false); // Something else changes it after the legitimate revert.
        tweak.Revert();

        Assert.Equal(false, fake.Read("device1"));
    }

    [Fact]
    public void GetStateReturnsUnknownWhenNoDeviceIsReadable()
    {
        var fake = new FakeDevicePower(("device1", null), ("device2", null));

        Assert.Equal(TweakState.Unknown, CreateTweak(fake).GetState());
    }

    /// <summary>Stands in for the real MSPower_DeviceEnable WMI lookups: a fixed set of instance IDs
    /// with an in-memory Enable state each (null = "could not be read", matching a missing/failed WMI
    /// object).</summary>
    private sealed class FakeDevicePower
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, bool?> _state = [];

        public FakeDevicePower(params (string InstanceId, bool? Enabled)[] devices)
        {
            foreach (var (instanceId, enabled) in devices)
            {
                _order.Add(instanceId);
                _state[instanceId] = enabled;
            }
        }

        public IReadOnlyList<string> InstanceIds() => _order;

        public bool? Read(string instanceId) => _state[instanceId];

        public void Write(string instanceId, bool value) => _state[instanceId] = value;
    }
}
