using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LatencyBench.Core.Interop;

/// <summary>
/// Storage device queries via DeviceIoControl.
/// <para>
/// The alternative — WMI's MSFT_PhysicalDisk — returns the same three facts but requires opening
/// the root\Microsoft\Windows\Storage namespace, which costs hundreds of milliseconds on first use.
/// These IOCTLs answer in microseconds and, for the properties used here, do not require elevation.
/// </para>
/// </summary>
internal static class StorageIoctl
{
    internal const uint IoctlStorageQueryProperty = 0x2D1400;

    internal const uint IoctlStorageGetDeviceNumber = 0x2D1080;

    /// <summary>
    /// IOCTL_DISK_GET_DRIVE_GEOMETRY_EX. Used in preference to IOCTL_DISK_GET_LENGTH_INFO because
    /// that one is defined with FILE_READ_ACCESS: it fails on a handle opened for zero access, and
    /// opening a physical drive for GENERIC_READ requires elevation. This one is FILE_ANY_ACCESS, so
    /// the size comes back for a standard user too.
    /// </summary>
    internal const uint IoctlDiskGetDriveGeometryEx = 0x700A0;

    /// <summary>Offset of DISK_GEOMETRY_EX.DiskSize — it follows the 24-byte DISK_GEOMETRY.</summary>
    internal const int DiskGeometryExDiskSizeOffset = 24;

    internal const uint StorageDeviceProperty = 0;

    internal const uint StorageDeviceSeekPenaltyProperty = 7;

    internal const uint PropertyStandardQuery = 0;

    internal const uint GenericRead = 0x80000000;

    internal const uint FileShareRead = 0x1;

    internal const uint FileShareWrite = 0x2;

    internal const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        ref STORAGE_PROPERTY_QUERY inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        internal uint PropertyId;
        internal uint QueryType;

        // AdditionalParameters[1] in the header. Present so the marshalled size matches what the
        // driver expects; never populated for the standard queries used here.
        internal byte AdditionalParameters;
        internal byte Padding0;
        internal byte Padding1;
        internal byte Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_DESCRIPTOR
    {
        internal uint Version;
        internal uint Size;
        internal byte DeviceType;
        internal byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] internal bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] internal bool CommandQueueing;
        internal uint VendorIdOffset;
        internal uint ProductIdOffset;
        internal uint ProductRevisionOffset;
        internal uint SerialNumberOffset;
        internal uint BusType;
        internal uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        internal uint Version;
        internal uint Size;
        [MarshalAs(UnmanagedType.U1)] internal bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_DEVICE_NUMBER
    {
        internal uint DeviceType;
        internal uint DeviceNumber;
        internal int PartitionNumber;
    }

    /// <summary>STORAGE_BUS_TYPE, from ntddstor.h.</summary>
    internal const uint BusTypeScsi = 0x1;
    internal const uint BusTypeAtapi = 0x2;
    internal const uint BusTypeAta = 0x3;
    internal const uint BusTypeUsb = 0x7;
    internal const uint BusTypeRaid = 0x8;
    internal const uint BusTypeSas = 0xA;
    internal const uint BusTypeSata = 0xB;
    internal const uint BusTypeNvme = 0x11;
}
