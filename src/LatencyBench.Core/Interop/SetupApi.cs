using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LatencyBench.Core.Interop;

internal static class SetupApi
{
    public const uint DIGCF_PRESENT = 2u;

    public const uint DIGCF_ALLCLASSES = 4u;

    public const uint DIGCF_DEVICEINTERFACE = 16u;

    public const uint DIF_PROPERTYCHANGE = 18u;

    public const uint DICS_PROPCHANGE = 3u;

    public const uint DICS_FLAG_CONFIGSPECIFIC = 2u;

    public static readonly Guid GUID_DEVCLASS_USB = new Guid("36FC9E60-C465-11CF-8056-444553540000");

    public static readonly Guid GUID_DEVINTERFACE_USB_HUB = new Guid("F18A0E88-C30C-11D0-8815-00A0C906BED8");

    public static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, nint deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiOpenDeviceInfoW(nint deviceInfoSet, string deviceInstanceId, nint hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiSetClassInstallParams(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, int classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiCallClassInstaller(uint installFunction, nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    public static Dictionary<uint, string> EnumerateDeviceInterfacePaths(Guid interfaceGuid)
    {
        Dictionary<uint, string> dictionary = new Dictionary<uint, string>();
        nint num = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, 18u);
        if (num == IntPtr.Zero || num == new IntPtr(-1))
        {
            return dictionary;
        }
        try
        {
            uint num2 = 0u;
            SP_DEVICE_INTERFACE_DATA deviceInterfaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };
            while (SetupDiEnumDeviceInterfaces(num, IntPtr.Zero, ref interfaceGuid, num2, ref deviceInterfaceData))
            {
                SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
                };
                SetupDiGetDeviceInterfaceDetail(num, ref deviceInterfaceData, IntPtr.Zero, 0u, out var requiredSize, ref deviceInfoData);
                if (requiredSize != 0)
                {
                    nint num3 = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(num3, (IntPtr.Size == 8) ? 8 : (4 + Marshal.SystemDefaultCharSize));
                        if (SetupDiGetDeviceInterfaceDetail(num, ref deviceInterfaceData, num3, requiredSize, out var _, ref deviceInfoData))
                        {
                            string? text = Marshal.PtrToStringUni(num3 + 4);
                            if (text != null)
                            {
                                dictionary[deviceInfoData.DevInst] = text;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(num3);
                    }
                }
                num2++;
                deviceInterfaceData.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(num);
        }
        return dictionary;
    }
}
