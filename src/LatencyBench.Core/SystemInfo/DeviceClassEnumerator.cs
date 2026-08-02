using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.SystemInfo;

/// <summary>One present device node, with the handful of properties every reader here needs.</summary>
public sealed record DeviceNodeSummary(
    uint DevInst,
    string InstanceId,
    string FriendlyName,
    string? DeviceClass,
    string? DriverKeyPath);

/// <summary>
/// Walks the devices of a setup class. Four readers had each written their own SetupDiGetClassDevs
/// loop with its own subtly different name-resolution fallback and its own chance of leaking the
/// device info set; this is the single implementation they now share.
/// </summary>
public static class DeviceClassEnumerator
{
    public static readonly Guid DisplayClass = new("4D36E968-E325-11CE-BFC1-08002BE10318");

    public static readonly Guid MediaClass = new("4D36E96C-E325-11CE-BFC1-08002BE10318");

    /// <summary>CM_DRP_DRIVER — the device's class-key subpath, "{class guid}\0000".</summary>
    private const uint CmDrpDriver = 10u;

    public static IReadOnlyList<DeviceNodeSummary> Enumerate(Guid classGuid)
    {
        var results = new List<DeviceNodeSummary>();

        nint deviceInfoSet = SetupApi.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            uint index = 0;
            var deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            while (SetupApi.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
            {
                uint devInst = deviceInfoData.DevInst;
                index++;

                // SetupDiEnumDeviceInfo overwrites the whole struct, so cbSize has to be restored
                // before the next call or enumeration stops after the first device.
                deviceInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

                string? instanceId = CfgMgr32.GetDeviceId(devInst);
                if (instanceId is null)
                {
                    continue;
                }

                results.Add(new DeviceNodeSummary(
                    devInst,
                    instanceId,
                    ResolveFriendlyName(devInst, instanceId),
                    CfgMgr32.GetStringProperty(devInst, CfgMgr32.CM_DRP_CLASS),
                    CfgMgr32.GetStringProperty(devInst, CmDrpDriver)));
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    /// <summary>
    /// Ordered best-to-worst. DEVPKEY_NAME is what Device Manager shows; the bus-reported description
    /// is the device's own string; the legacy friendly name and device description are the fallbacks
    /// for drivers that populate neither.
    /// </summary>
    public static string ResolveFriendlyName(uint devInst, string instanceId)
    {
        return Clean(CfgMgr32.GetName(devInst))
            ?? Clean(CfgMgr32.GetBusReportedDeviceDesc(devInst))
            ?? Clean(CfgMgr32.GetStringProperty(devInst, CfgMgr32.CM_DRP_FRIENDLYNAME))
            ?? Clean(CfgMgr32.GetStringProperty(devInst, CfgMgr32.CM_DRP_DEVICEDESC))
            ?? instanceId;
    }

    /// <summary>
    /// Walks up the device tree to the nearest ancestor on the PCI bus, or returns null if there
    /// isn't one.
    /// <para>
    /// Needed because the interesting node and the node with the interrupt are usually not the same
    /// device. Onboard audio enumerates as an HDAUDIO codec whose instance ID starts "HDAUDIO\",
    /// while the interrupt belongs to the HD Audio controller one level up on PCI. Testing the
    /// device's own instance ID for a "PCI\" prefix therefore classifies every onboard audio device
    /// as non-PCI, which is how the first version of this got it wrong.
    /// </para>
    /// </summary>
    public static string? FindPciAncestor(uint devInst)
    {
        // Bounded rather than while(true): a corrupt device tree with a parent cycle would otherwise
        // hang the detection pass, and no real tree is anywhere near this deep.
        const int maxDepth = 16;

        uint current = devInst;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            if (CfgMgr32.CM_Get_Parent(out uint parent, current, 0) != CfgMgr32.CR_SUCCESS)
            {
                return null;
            }

            string? parentId = CfgMgr32.GetDeviceId(parent);
            if (parentId is null)
            {
                return null;
            }

            if (parentId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            {
                return parentId;
            }

            // Stop at the USB boundary. Every USB device eventually has a PCI ancestor — the host
            // controller — so without this the walk reports a USB microphone as PCI-attached and the
            // distinction this method exists to draw disappears. A USB device's interrupts belong to
            // its host controller, which is tuned as a USB controller rather than as this device.
            if (parentId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // The root node is its own conceptual ceiling; going further is pointless.
            if (parentId.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static string? Clean(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
