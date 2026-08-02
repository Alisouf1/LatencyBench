using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using LatencyBench.Core.Interop;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.Msi;

public sealed class InterruptDeviceEnumerator
{
    private const string MsiRelativePath = "Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";

    private const string PriorityRelativePath = "Device Parameters\\Interrupt Management\\Affinity Policy";

    public List<InterruptDeviceInfo> EnumerateInterruptCapableDevices()
    {
        List<InterruptDeviceInfo> list = new List<InterruptDeviceInfo>();
        Guid classGuid = Guid.Empty;
        nint num = SetupApi.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 6u);
        if (num == IntPtr.Zero || num == new IntPtr(-1))
        {
            return list;
        }
        try
        {
            uint num2 = 0u;
            SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            while (SetupApi.SetupDiEnumDeviceInfo(num, num2, ref deviceInfoData))
            {
                uint devInst = deviceInfoData.DevInst;
                num2++;
                deviceInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
                string? deviceId = CfgMgr32.GetDeviceId(devInst);
                if (deviceId != null && HasMsiCapability(deviceId))
                {
                    string friendlyName = Clean(CfgMgr32.GetName(devInst)) ?? Clean(CfgMgr32.GetBusReportedDeviceDesc(devInst)) ?? Clean(CfgMgr32.GetStringProperty(devInst, 13u)) ?? Clean(CfgMgr32.GetStringProperty(devInst, 1u)) ?? deviceId;
                    string categoryLabel = CategoryLabel(CfgMgr32.GetStringProperty(devInst, 8u), friendlyName);
                    list.Add(new InterruptDeviceInfo(deviceId, friendlyName, categoryLabel, ReadMsiSupported(deviceId) == 1, ReadPriority(deviceId), ReadServiceName(deviceId)));
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(num);
        }
        return list.OrderBy<InterruptDeviceInfo, string>((InterruptDeviceInfo r) => r.CategoryLabel, StringComparer.OrdinalIgnoreCase).ThenBy<InterruptDeviceInfo, string>((InterruptDeviceInfo r) => r.FriendlyName, StringComparer.OrdinalIgnoreCase).ThenBy<InterruptDeviceInfo, string>((InterruptDeviceInfo r) => r.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Clean(string? name)
    {
        string? text = name?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string CategoryLabel(string? deviceClass, string friendlyName)
    {
        if (friendlyName.Contains("Audio", StringComparison.OrdinalIgnoreCase) && friendlyName.Contains("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return "Audio controller";
        }
        string result;
        switch (deviceClass)
        {
            case "USB":
                result = "USB controller";
                break;
            case "Display":
                result = "GPU";
                break;
            case "Net":
                result = "Network adapter";
                break;
            case "Media":
            case "AudioEndpoint":
                result = "Audio controller";
                break;
            case "SCSIAdapter":
            case "HDC":
                result = "Storage controller";
                break;
            case "Bluetooth":
                result = "Bluetooth adapter";
                break;
            case "System":
                result = "System device";
                break;
            case null:
                result = "Device";
                break;
            default:
                result = deviceClass;
                break;
        }
        return result;
    }

    private static bool HasMsiCapability(string instanceId)
    {
        // Disposed rather than discarded: this runs once per device across the entire device tree,
        // so leaking the handle here leaked several hundred open registry keys per enumeration, and
        // the tab re-enumerates on every refresh.
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            "SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties");
        return key is not null;
    }

    private static int? ReadMsiSupported(string instanceId)
    {
        using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties");
        return (registryKey?.GetValue("MSISupported") is int value) ? new int?(value) : ((int?)null);
    }

    private static string? ReadServiceName(string instanceId)
    {
        using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId);
        return registryKey?.GetValue("Service") as string;
    }

    private static InterruptPriority ReadPriority(string instanceId)
    {
        using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy");
        return (registryKey?.GetValue("DevicePriority") is int num && Enum.IsDefined(typeof(InterruptPriority), num)) ? ((InterruptPriority)num) : InterruptPriority.Undefined;
    }
}
