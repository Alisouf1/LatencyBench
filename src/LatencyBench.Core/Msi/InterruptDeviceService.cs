using System;
using Microsoft.Win32;
using LatencyBench.Core.Devices;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.Msi;

/// <summary>
/// Writes interrupt policy (MSI mode, interrupt priority) to a device's registry node.
/// </summary>
public sealed class InterruptDeviceService
{
    private const string EnumRootPath = @"SYSTEM\CurrentControlSet\Enum";

    private const string MsiRelativePath = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    private const string PriorityRelativePath = @"Device Parameters\Interrupt Management\Affinity Policy";

    public void SetMsiMode(string instanceId, bool enabled)
    {
        using RegistryKey policyKey = OpenPolicyKeyOfExistingDevice(instanceId, MsiRelativePath);
        policyKey.SetValue("MSISupported", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public void SetPriority(string instanceId, InterruptPriority priority)
    {
        // The value is cast straight to int and written as a DWORD, so an undefined enum value would
        // be stored verbatim and interpreted by the kernel as a priority that does not exist.
        if (!Enum.IsDefined(typeof(InterruptPriority), priority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority), priority, "Unknown interrupt priority.");
        }

        using RegistryKey policyKey = OpenPolicyKeyOfExistingDevice(instanceId, PriorityRelativePath);
        policyKey.SetValue("DevicePriority", (int)priority, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Resolves the interrupt-policy key for a device that must already exist, creating only the
    /// policy subkeys beneath it.
    ///
    /// <para>
    /// Both methods previously called CreateSubKey on the entire path in one go:
    /// <c>CreateSubKey("SYSTEM\CurrentControlSet\Enum\" + instanceId + "\Device Parameters\...")</c>.
    /// CreateSubKey creates every missing level of the path it is given, so an instance ID for a
    /// device that is not present did not fail — it fabricated a device node inside the live Windows
    /// device tree and attached interrupt-policy values to hardware that does not exist. A stale ID,
    /// an unplugged device, or a profile saved on another machine was enough to trigger it, nothing
    /// reported an error, and the junk survived reboots.
    /// </para>
    ///
    /// <para>
    /// Opening the device node first makes its existence a precondition rather than a side effect.
    /// This is the pattern InterruptAffinityService.SetSpecifiedCores already used; the two are now
    /// consistent.
    /// </para>
    /// </summary>
    private static RegistryKey OpenPolicyKeyOfExistingDevice(string instanceId, string relativePath)
    {
        DeviceInstanceId.ThrowIfInvalid(instanceId, nameof(instanceId));

        using RegistryKey enumRoot = Registry.LocalMachine.OpenSubKey(EnumRootPath)
            ?? throw new InvalidOperationException(
                $@"HKLM\{EnumRootPath} could not be opened. This process is not elevated, or the " +
                "device tree is unavailable.");

        using RegistryKey deviceKey = enumRoot.OpenSubKey(instanceId, writable: true)
            ?? throw new InvalidOperationException(
                $"Device '{instanceId}' is not present in the Windows device tree, so its interrupt " +
                "policy cannot be changed. It may have been removed since the list was last refreshed.");

        return deviceKey.CreateSubKey(relativePath, writable: true);
    }
}
