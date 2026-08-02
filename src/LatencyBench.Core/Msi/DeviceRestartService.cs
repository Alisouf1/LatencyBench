using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Msi;

public sealed class DeviceRestartService
{
    private static readonly HashSet<string> RestartableCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "USB controller", "Network adapter", "Audio controller" };

    private static readonly HashSet<string> ConfirmFirstCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GPU" };

    public static bool IsRestartable(string categoryLabel)
    {
        return RestartableCategories.Contains(categoryLabel) || ConfirmFirstCategories.Contains(categoryLabel);
    }

    public static bool RequiresConfirmation(string categoryLabel)
    {
        return ConfirmFirstCategories.Contains(categoryLabel);
    }

    public void Restart(string instanceId)
    {
        nint num = SetupApi.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (num == IntPtr.Zero || num == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a device info list.");
        }
        try
        {
            SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };
            if (!SetupApi.SetupDiOpenDeviceInfoW(num, instanceId, IntPtr.Zero, 0u, ref deviceInfoData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open device '" + instanceId + "'.");
            }
            SP_PROPCHANGE_PARAMS classInstallParams = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = 18u
                },
                StateChange = 3u,
                Scope = 2u,
                HwProfile = 0u
            };
            if (!SetupApi.SetupDiSetClassInstallParams(num, ref deviceInfoData, ref classInstallParams, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stage the restart request.");
            }
            if (!SetupApi.SetupDiCallClassInstaller(18u, num, ref deviceInfoData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The device refused to restart. A reboot will still apply the change.");
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(num);
        }
    }
}
