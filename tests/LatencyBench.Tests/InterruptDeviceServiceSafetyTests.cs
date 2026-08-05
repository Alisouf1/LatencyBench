using System;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using Microsoft.Win32;

namespace LatencyBench.Tests;

/// <summary>
/// Proves InterruptDeviceService refuses to write interrupt policy for a device that is not present,
/// and that it rejects malformed IDs before touching the registry at all.
///
/// <para>
/// The behaviour being prevented was verified against the real registry rather than assumed:
/// RegistryKey.CreateSubKey creates every missing level of the path it is given. The previous
/// implementation passed the whole path — enum root, instance ID and policy subkeys — to CreateSubKey
/// in a single call, so an ID for absent hardware fabricated a device node inside the live Windows
/// device tree and attached interrupt values to it. No error was raised and the entry persisted
/// across reboots.
/// </para>
///
/// <para>
/// Every ID used here is deliberately non-existent, so a regression cannot damage the machine running
/// the tests: with the fix the call throws, and the assertions below confirm nothing was written.
/// </para>
/// </summary>
public sealed class InterruptDeviceServiceSafetyTests
{
    private const string EnumRootPath = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>Well-formed, and certain not to exist — VEN_FFFF/DEV_FFFF is not an allocated PCI ID.</summary>
    private const string AbsentButWellFormedId = @"PCI\VEN_FFFF&DEV_FFFF&SUBSYS_FFFFFFFF&REV_FF\0&latencybenchtest&0&FF";

    private static bool KeyExists(string instanceId)
    {
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(EnumRootPath);
        using RegistryKey? key = root?.OpenSubKey(instanceId);
        return key is not null;
    }

    // --- Malformed IDs are rejected before any registry access ---------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PCI")]
    [InlineData(@"\PCI\VEN_1022\3&115")]
    [InlineData(@"PCI\VEN_1022\3&115\")]
    [InlineData(@"PCI\..\..\SYSTEM\CurrentControlSet\Services")]
    [InlineData(@"PCI\VEN_1022\3&115 with spaces")]
    [InlineData(@"PCI\VEN_1022\3&115/slash")]
    public void SetMsiModeRejectsMalformedIdsWithArgumentException(string? instanceId)
    {
        var service = new InterruptDeviceService();

        Assert.Throws<ArgumentException>(() => service.SetMsiMode(instanceId!, enabled: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PCI")]
    [InlineData(@"PCI\..\..\SYSTEM")]
    public void SetPriorityRejectsMalformedIdsWithArgumentException(string? instanceId)
    {
        var service = new InterruptDeviceService();

        Assert.Throws<ArgumentException>(() => service.SetPriority(instanceId!, InterruptPriority.High));
    }

    // --- An undefined enum value is refused rather than written verbatim ------------------------

    [Fact]
    public void SetPriorityRejectsAnUndefinedPriorityValue()
    {
        // The value is cast to int and stored as a DWORD, so an out-of-range value would be written
        // as a priority the kernel has no definition for.
        var service = new InterruptDeviceService();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.SetPriority(@"PCI\VEN_1022\3&115", (InterruptPriority)9999));
    }

    // --- The core regression: absent hardware must not be invented ------------------------------

    [Fact]
    public void SetMsiModeDoesNotFabricateADeviceNodeForAbsentHardware()
    {
        Assert.False(
            KeyExists(AbsentButWellFormedId),
            "Precondition failed: the test device id already exists. Aborting rather than risking a real write.");

        var service = new InterruptDeviceService();

        // Throws either because the device is absent, or - when the tests run unelevated - because the
        // Enum root cannot be opened writable. Both are correct refusals; neither may create anything.
        Assert.ThrowsAny<Exception>(() => service.SetMsiMode(AbsentButWellFormedId, enabled: true));

        Assert.False(
            KeyExists(AbsentButWellFormedId),
            "REGRESSION: a device node was created in the live Windows device tree for hardware that " +
            "does not exist.");
    }

    [Fact]
    public void SetPriorityDoesNotFabricateADeviceNodeForAbsentHardware()
    {
        Assert.False(KeyExists(AbsentButWellFormedId), "Precondition failed: the test device id already exists.");

        var service = new InterruptDeviceService();

        Assert.ThrowsAny<Exception>(() => service.SetPriority(AbsentButWellFormedId, InterruptPriority.High));

        Assert.False(
            KeyExists(AbsentButWellFormedId),
            "REGRESSION: a device node was created for hardware that does not exist.");
    }

    [Fact]
    public void TheRefusalForAbsentHardwareSaysWhyRatherThanFailingOpaquely()
    {
        var service = new InterruptDeviceService();

        var ex = Assert.ThrowsAny<Exception>(() => service.SetMsiMode(AbsentButWellFormedId, enabled: true));

        // Whichever refusal fired, the message has to name the actual problem: the device is not
        // there, or this process cannot write to the device tree.
        Assert.True(
            ex.Message.Contains("not present", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not elevated", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("could not be opened", StringComparison.OrdinalIgnoreCase),
            $"Unhelpful failure message: {ex.Message}");
    }
}
