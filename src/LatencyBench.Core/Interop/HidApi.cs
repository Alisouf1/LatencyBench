using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LatencyBench.Core.Interop;

internal static class HidApi
{
    private const int HIDP_STATUS_SUCCESS = 1114112;

    private const uint GENERIC_READ = 2147483648u;

    private const uint FILE_SHARE_READ = 1u;

    private const uint FILE_SHARE_WRITE = 2u;

    private const uint OPEN_EXISTING = 3u;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out nint preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, out HIDP_CAPS capabilities);

    public static (ushort UsagePage, ushort Usage)? TryGetUsage(string devicePath)
    {
        using SafeFileHandle safeFileHandle = CreateFileW(devicePath, 2147483648u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            return null;
        }
        if (!HidD_GetPreparsedData(safeFileHandle, out var preparsedData))
        {
            return null;
        }
        try
        {
            HIDP_CAPS capabilities;
            return (HidP_GetCaps(preparsedData, out capabilities) == 1114112) ? new (ushort, ushort)?((capabilities.UsagePage, capabilities.Usage)) : (((ushort, ushort)?)null);
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }
}
