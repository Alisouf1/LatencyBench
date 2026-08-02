using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.Interop;

internal static class UsbIoctl
{
    public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 2229320u;

    private const uint GENERIC_WRITE = 1073741824u;

    private const uint FILE_SHARE_WRITE = 2u;

    private const uint OPEN_EXISTING = 3u;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize, nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    public static UsbSpeed TryGetSpeed(string hubDevicePath, int portNumber)
    {
        using SafeFileHandle safeFileHandle = CreateFileW(hubDevicePath, 1073741824u, 2u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            return UsbSpeed.Unknown;
        }
        int num = Marshal.SizeOf<USB_NODE_CONNECTION_INFORMATION_EX>();
        nint num2 = Marshal.AllocHGlobal(num);
        try
        {
            Marshal.WriteInt32(num2, portNumber);
            if (!DeviceIoControl(safeFileHandle, 2229320u, num2, (uint)num, num2, (uint)num, out var _, IntPtr.Zero))
            {
                return UsbSpeed.Unknown;
            }
            byte speed = Marshal.PtrToStructure<USB_NODE_CONNECTION_INFORMATION_EX>(num2).Speed;
            UsbSpeed result = speed switch
            {
                0 => UsbSpeed.Low,
                1 => UsbSpeed.Full,
                2 => UsbSpeed.High,
                3 => UsbSpeed.Super,
                4 => UsbSpeed.SuperPlus,
                _ => UsbSpeed.Unknown,
            };
            return result;
        }
        catch
        {
            return UsbSpeed.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(num2);
        }
    }
}
