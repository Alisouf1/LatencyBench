using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;
using LatencyBench.Core.SystemInfo.Models;
using Microsoft.Win32.SafeHandles;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Enumerates physical drives, their bus type, whether they are solid state, and which drive
/// letters they back.
/// </summary>
public sealed class StorageEnumerator
{
    /// <summary>
    /// Physical drive numbers are not dense — removing a drive leaves a gap — so probing walks a
    /// fixed range and skips failures rather than stopping at the first miss. 64 is well past any
    /// realistic consumer or workstation configuration.
    /// </summary>
    private const int MaxPhysicalDriveNumber = 64;

    public IReadOnlyList<StorageDeviceInfo> Enumerate()
    {
        var lettersByDeviceNumber = MapDriveLetters();
        var results = new List<StorageDeviceInfo>();

        for (int driveNumber = 0; driveNumber < MaxPhysicalDriveNumber; driveNumber++)
        {
            string path = $@"\\.\PhysicalDrive{driveNumber.ToString(CultureInfo.InvariantCulture)}";

            // No GENERIC_READ: opening for zero access still allows property IOCTLs and, unlike
            // GENERIC_READ, does not require elevation. Asking for read access here is what makes
            // this kind of probe fail for a standard user.
            using SafeFileHandle handle = StorageIoctl.CreateFileW(
                path,
                desiredAccess: 0,
                StorageIoctl.FileShareRead | StorageIoctl.FileShareWrite,
                IntPtr.Zero,
                StorageIoctl.OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                continue;
            }

            var descriptor = QueryDeviceDescriptor(handle);
            if (descriptor is null)
            {
                continue;
            }

            lettersByDeviceNumber.TryGetValue(driveNumber, out var letters);

            results.Add(new StorageDeviceInfo
            {
                FriendlyName = descriptor.Value.Name,
                InstanceId = path,
                BusType = MapBusType(descriptor.Value.BusType),
                IsSolidState = QueryIsSolidState(handle, MapBusType(descriptor.Value.BusType)),
                SizeBytes = QueryLengthBytes(handle),
                DriveLetters = letters ?? (IReadOnlyList<string>)Array.Empty<string>()
            });
        }

        return results;
    }

    private static (string Name, uint BusType)? QueryDeviceDescriptor(SafeFileHandle handle)
    {
        var query = new StorageIoctl.STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageIoctl.StorageDeviceProperty,
            QueryType = StorageIoctl.PropertyStandardQuery
        };

        // The descriptor is variable length: the fixed header is followed by the vendor/product
        // strings it points at with byte offsets, so a generous single buffer is simpler and cheaper
        // than the documented two-call size probe.
        const int bufferSize = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!StorageIoctl.DeviceIoControl(
                handle,
                StorageIoctl.IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StorageIoctl.STORAGE_PROPERTY_QUERY>(),
                buffer,
                bufferSize,
                out uint returned,
                IntPtr.Zero) || returned < Marshal.SizeOf<StorageIoctl.STORAGE_DEVICE_DESCRIPTOR>())
            {
                return null;
            }

            var descriptor = Marshal.PtrToStructure<StorageIoctl.STORAGE_DEVICE_DESCRIPTOR>(buffer);
            string vendor = ReadOffsetString(buffer, descriptor.VendorIdOffset, returned);
            string product = ReadOffsetString(buffer, descriptor.ProductIdOffset, returned);

            string name = string.Join(' ', vendor, product).Trim();
            return (string.IsNullOrWhiteSpace(name) ? "Unknown drive" : name, descriptor.BusType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadOffsetString(IntPtr buffer, uint offset, uint validLength)
    {
        // An offset of zero means "not present", and any offset past what the driver actually wrote
        // would read unrelated memory.
        if (offset == 0 || offset >= validLength)
        {
            return string.Empty;
        }

        return (Marshal.PtrToStringAnsi(buffer + (int)offset) ?? string.Empty).Trim();
    }

    private static bool? QueryIsSolidState(SafeFileHandle handle, StorageBusType busType)
    {
        // NVMe is solid state by definition — there is no such thing as a spinning NVMe drive — and
        // some NVMe drivers do not answer the seek-penalty query at all.
        if (busType == StorageBusType.Nvme)
        {
            return true;
        }

        var query = new StorageIoctl.STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageIoctl.StorageDeviceSeekPenaltyProperty,
            QueryType = StorageIoctl.PropertyStandardQuery
        };

        int size = Marshal.SizeOf<StorageIoctl.DEVICE_SEEK_PENALTY_DESCRIPTOR>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!StorageIoctl.DeviceIoControl(
                handle,
                StorageIoctl.IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StorageIoctl.STORAGE_PROPERTY_QUERY>(),
                buffer,
                size,
                out uint returned,
                IntPtr.Zero) || returned < size)
            {
                // Genuinely unknown — reported as null rather than guessed, because callers use this
                // to decide whether an SSD-only recommendation applies.
                return null;
            }

            var descriptor = Marshal.PtrToStructure<StorageIoctl.DEVICE_SEEK_PENALTY_DESCRIPTOR>(buffer);
            return !descriptor.IncursSeekPenalty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ulong? QueryLengthBytes(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX is variable length — the fixed part is followed by partition and detection
        // data — so the buffer is sized generously rather than to the struct.
        const int bufferSize = 256;
        const int minimumUseful = StorageIoctl.DiskGeometryExDiskSizeOffset + sizeof(long);

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!StorageIoctl.DeviceIoControl(
                handle,
                StorageIoctl.IoctlDiskGetDriveGeometryEx,
                IntPtr.Zero,
                0,
                buffer,
                bufferSize,
                out uint returned,
                IntPtr.Zero) || returned < minimumUseful)
            {
                return null;
            }

            long length = Marshal.ReadInt64(buffer, StorageIoctl.DiskGeometryExDiskSizeOffset);
            return length > 0 ? (ulong)length : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Builds physical-drive-number to drive-letter mapping by asking each volume which device it
    /// sits on. A volume spanning several disks reports the first, which is the behaviour wanted
    /// here: the goal is to label a drive "C:" for the user, not to describe spanned sets exactly.
    /// </summary>
    private static Dictionary<int, List<string>> MapDriveLetters()
    {
        var map = new Dictionary<int, List<string>>();

        foreach (DriveInfo drive in SafeGetDrives())
        {
            string root = drive.Name.TrimEnd('\\');
            if (root.Length != 2 || root[1] != ':')
            {
                continue;
            }

            using SafeFileHandle handle = StorageIoctl.CreateFileW(
                $@"\\.\{root}",
                desiredAccess: 0,
                StorageIoctl.FileShareRead | StorageIoctl.FileShareWrite,
                IntPtr.Zero,
                StorageIoctl.OpenExisting,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                continue;
            }

            int size = Marshal.SizeOf<StorageIoctl.STORAGE_DEVICE_NUMBER>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (StorageIoctl.DeviceIoControl(
                    handle,
                    StorageIoctl.IoctlStorageGetDeviceNumber,
                    IntPtr.Zero,
                    0,
                    buffer,
                    size,
                    out uint returned,
                    IntPtr.Zero) && returned >= size)
                {
                    var number = Marshal.PtrToStructure<StorageIoctl.STORAGE_DEVICE_NUMBER>(buffer);
                    int deviceNumber = (int)number.DeviceNumber;
                    if (!map.TryGetValue(deviceNumber, out var letters))
                    {
                        letters = new List<string>();
                        map[deviceNumber] = letters;
                    }

                    letters.Add(root);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return map;
    }

    private static DriveInfo[] SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            // A drive disappearing mid-enumeration must not take out the whole hardware profile.
            return Array.Empty<DriveInfo>();
        }
    }

    private static StorageBusType MapBusType(uint busType) => busType switch
    {
        StorageIoctl.BusTypeScsi => StorageBusType.Scsi,
        StorageIoctl.BusTypeAtapi or StorageIoctl.BusTypeAta => StorageBusType.Ata,
        StorageIoctl.BusTypeUsb => StorageBusType.Usb,
        StorageIoctl.BusTypeRaid => StorageBusType.Raid,
        StorageIoctl.BusTypeSas => StorageBusType.Sas,
        StorageIoctl.BusTypeSata => StorageBusType.Sata,
        StorageIoctl.BusTypeNvme => StorageBusType.Nvme,
        _ => StorageBusType.Unknown
    };
}
