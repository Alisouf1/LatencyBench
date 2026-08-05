using System;
using LatencyBench.Core.Devices;

namespace LatencyBench.Tests;

/// <summary>
/// Guards the validation applied to every device instance ID before it is used to build a registry
/// path under HKLM\SYSTEM\CurrentControlSet\Enum.
///
/// The defect being prevented is not path traversal — that was tested against the real registry and
/// does not exist: ".." is a literal key name there, and a malformed ID cannot escape the Enum
/// subtree. It is that RegistryKey.CreateSubKey creates every missing level of a path, so an ID for
/// a device that is not present silently fabricates a device node in the live Windows device tree
/// instead of failing.
/// </summary>
public sealed class DeviceInstanceIdTests
{
    // --- Happy path: IDs of the exact shape Windows device enumeration produces -----------------

    [Theory]
    [InlineData(@"PCI\VEN_1022&DEV_43EE&SUBSYS_11421B21&REV_01\3&11583659&0&E0")]
    [InlineData(@"USB\VID_046D&PID_C08B&MI_00\7&2A5F1C3E&0&0000")]
    [InlineData(@"HID\VID_3151&PID_4030&MI_01&COL01\8&1B0F4B9C&0&0000")]
    [InlineData(@"ACPI\PNP0C02\1")]
    [InlineData(@"ROOT\SYSTEM\0000")]
    [InlineData(@"SWD\PRINTENUM\{28D74F13-F627-4A4B-8FC1-B9C2F9A2C0E7}")]
    [InlineData(@"PCI\VEN_10DE&DEV_2504\4&1A2B3C4D&0&0008")]
    public void RealWorldInstanceIdsAreAccepted(string instanceId)
    {
        Assert.True(DeviceInstanceId.IsValid(instanceId), $"'{instanceId}' should be valid.");
    }

    // --- Null / empty / whitespace ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void NullEmptyAndWhitespaceAreRejected(string? instanceId)
    {
        Assert.False(DeviceInstanceId.IsValid(instanceId));
    }

    // --- Structural rules ------------------------------------------------------------------------

    [Theory]
    [InlineData(@"PCI", "single segment targets the enumerator key shared by every device on that bus")]
    [InlineData(@"\PCI\VEN_1022\3&11583659", "leading backslash")]
    [InlineData(@"PCI\VEN_1022\3&11583659\", "trailing backslash")]
    [InlineData(@"PCI\\VEN_1022\3&11583659", "empty segment from a doubled backslash")]
    public void StructurallyMalformedIdsAreRejected(string instanceId, string why)
    {
        Assert.False(DeviceInstanceId.IsValid(instanceId), $"should reject: {why}");
    }

    // --- Relative segments -----------------------------------------------------------------------
    //
    // These do NOT traverse in the registry - verified against the real registry, where ".." is
    // created as a literal key. They are rejected because device enumeration never produces them,
    // so their presence proves the value did not come from where the caller believes it did.

    [Theory]
    [InlineData(@"PCI\..\..\SYSTEM\CurrentControlSet\Services")]
    [InlineData(@"PCI\VEN_1022\..")]
    [InlineData(@"..\PCI\VEN_1022")]
    [InlineData(@"PCI\.\VEN_1022")]
    [InlineData(@"PCI\VEN_1022\.")]
    public void RelativeSegmentsAreRejected(string instanceId)
    {
        Assert.False(DeviceInstanceId.IsValid(instanceId));
    }

    // --- Character whitelist ---------------------------------------------------------------------

    [Theory]
    [InlineData(@"PCI\VEN_1022\3&115 83659", "space")]
    [InlineData("PCI\\VEN_1022\\3&115\083659", "embedded NUL")]
    [InlineData(@"PCI\VEN_1022\3&115/83659", "forward slash")]
    [InlineData(@"PCI\VEN_1022\3&115:83659", "colon")]
    [InlineData(@"PCI\VEN_1022\3&115*83659", "wildcard asterisk")]
    [InlineData(@"PCI\VEN_1022\3&115?83659", "wildcard question mark")]
    [InlineData(@"PCI\VEN_1022\3&115""83659", "quote")]
    [InlineData("PCI\\VEN_1022\\3&115\r83659", "carriage return")]
    [InlineData("PCI\\VEN_1022\\3&115\n83659", "line feed")]
    [InlineData(@"PCI\VEN_1022\3&115%83659", "percent")]
    [InlineData(@"PCI\VEN_1022\3&115$83659", "dollar")]
    public void CharactersOutsideTheWhitelistAreRejected(string instanceId, string why)
    {
        Assert.False(DeviceInstanceId.IsValid(instanceId), $"should reject: {why}");
    }

    // --- Length boundaries -----------------------------------------------------------------------

    [Fact]
    public void AnIdOfExactlyTheMaximumLengthIsAccepted()
    {
        // MAX_DEVICE_ID_LEN is inclusive, so the boundary value itself must pass.
        string id = @"PCI\VEN_1022\" + new string('A', DeviceInstanceId.MaxLength - @"PCI\VEN_1022\".Length);

        Assert.Equal(DeviceInstanceId.MaxLength, id.Length);
        Assert.True(DeviceInstanceId.IsValid(id));
    }

    [Fact]
    public void AnIdOneCharacterOverTheMaximumIsRejected()
    {
        string id = @"PCI\VEN_1022\" + new string('A', DeviceInstanceId.MaxLength - @"PCI\VEN_1022\".Length + 1);

        Assert.Equal(DeviceInstanceId.MaxLength + 1, id.Length);
        Assert.False(DeviceInstanceId.IsValid(id));
    }

    [Fact]
    public void AGrosslyOversizedIdIsRejectedWithoutScanningItAll()
    {
        // Guards against a caller passing an enormous string: the length check must come before the
        // per-character scan so this is O(1) rather than O(n).
        Assert.False(DeviceInstanceId.IsValid(@"PCI\" + new string('A', 1_000_000)));
    }

    [Fact]
    public void TheShortestLegalIdIsAccepted()
    {
        Assert.True(DeviceInstanceId.IsValid(@"A\B"));
    }

    // --- Exception behaviour ---------------------------------------------------------------------

    [Fact]
    public void ThrowIfInvalidNamesTheParameterAndExplainsTheRule()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DeviceInstanceId.ThrowIfInvalid(@"PCI\VEN_1022\3&115 83659", "instanceId"));

        Assert.Equal("instanceId", ex.ParamName);
        Assert.Contains("not valid in a device instance ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalidReportsNullReadably()
    {
        var ex = Assert.Throws<ArgumentException>(() => DeviceInstanceId.ThrowIfInvalid(null, "instanceId"));

        Assert.Contains("(null)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalidAcceptsAValidIdSilently()
    {
        Exception? failure = Record.Exception(
            () => DeviceInstanceId.ThrowIfInvalid(@"PCI\VEN_1022&DEV_43EE\3&11583659&0&E0", "instanceId"));

        Assert.Null(failure);
    }
}
