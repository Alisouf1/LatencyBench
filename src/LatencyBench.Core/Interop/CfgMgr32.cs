using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LatencyBench.Core.Interop;

internal static class CfgMgr32
{
    public const uint CR_SUCCESS = 0u;

    public const uint CR_NO_SUCH_VALUE = 37u;

    public const uint CR_NO_SUCH_DEVNODE = 13u;

    public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new DEVPROPKEY
    {
        fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
        pid = 4u
    };

    public static readonly DEVPROPKEY DEVPKEY_NAME = new DEVPROPKEY
    {
        fmtid = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
        pid = 10u
    };

    public const uint CM_DRP_DEVICEDESC = 1u;

    public const uint CM_DRP_CLASS = 8u;

    public const uint CM_DRP_FRIENDLYNAME = 13u;

    public const uint CM_DRP_ADDRESS = 29u;

    public const uint CM_LOCATE_DEVNODE_NORMAL = 0u;

    public const uint DN_STARTED = 8u;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType, nint buffer, ref uint pulLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, out ulong propertyType, nint propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    public static string? GetBusReportedDeviceDesc(uint devInst)
    {
        return GetDevProperty(devInst, DEVPKEY_Device_BusReportedDeviceDesc);
    }

    public static string? GetName(uint devInst)
    {
        return GetDevProperty(devInst, DEVPKEY_NAME);
    }

    private static string? GetDevProperty(uint devInst, DEVPROPKEY property)
    {
        uint propertyBufferSize = 0u;
        DEVPROPKEY propertyKey = property;
        CM_Get_DevNode_PropertyW(devInst, ref propertyKey, out var propertyType, IntPtr.Zero, ref propertyBufferSize, 0u);
        if (propertyBufferSize == 0)
        {
            return null;
        }
        nint num = Marshal.AllocHGlobal((int)propertyBufferSize);
        try
        {
            return (CM_Get_DevNode_PropertyW(devInst, ref propertyKey, out propertyType, num, ref propertyBufferSize, 0u) == 0) ? Marshal.PtrToStringUni(num) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(num);
        }
    }

    /// <summary>
    /// DEVPKEY_Device_Numa_Node. The NUMA node the device is physically attached to, which on a
    /// multi-socket or multi-die machine is not necessarily the node whose cores are free.
    /// </summary>
    public static readonly DEVPROPKEY DEVPKEY_Device_Numa_Node = new DEVPROPKEY
    {
        fmtid = new Guid("540B947E-8B40-45BC-A8A2-6A0B894CBDA2"),
        pid = 3u
    };

    /// <summary>
    /// Reads a DEVPROP_TYPE_UINT32 device property. Returns null when the device does not expose it,
    /// which is the normal case for NUMA node on a single-node consumer machine.
    /// </summary>
    public static uint? GetUInt32Property(uint devInst, DEVPROPKEY property)
    {
        uint bufferSize = sizeof(uint);
        DEVPROPKEY propertyKey = property;
        nint buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            if (CM_Get_DevNode_PropertyW(devInst, ref propertyKey, out _, buffer, ref bufferSize, 0u) != CR_SUCCESS
                || bufferSize < sizeof(uint))
            {
                return null;
            }

            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? GetDeviceId(uint devInst)
    {
        StringBuilder stringBuilder = new StringBuilder(512);
        return (CM_Get_Device_IDW(devInst, stringBuilder, (uint)stringBuilder.Capacity, 0u) == 0) ? stringBuilder.ToString() : null;
    }

    public static string? GetStringProperty(uint devInst, uint property)
    {
        uint pulLength = 0u;
        uint pulRegDataType;
        uint num = CM_Get_DevNode_Registry_PropertyW(devInst, property, out pulRegDataType, IntPtr.Zero, ref pulLength, 0u);
        if (pulLength == 0)
        {
            return null;
        }
        nint num2 = Marshal.AllocHGlobal((int)pulLength);
        try
        {
            return (CM_Get_DevNode_Registry_PropertyW(devInst, property, out pulRegDataType, num2, ref pulLength, 0u) == 0) ? Marshal.PtrToStringUni(num2) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(num2);
        }
    }

    public static int? GetDwordProperty(uint devInst, uint property)
    {
        uint pulLength = 4u;
        nint num = Marshal.AllocHGlobal((int)pulLength);
        try
        {
            uint pulRegDataType;
            return (CM_Get_DevNode_Registry_PropertyW(devInst, property, out pulRegDataType, num, ref pulLength, 0u) == 0) ? new int?(Marshal.ReadInt32(num)) : ((int?)null);
        }
        finally
        {
            Marshal.FreeHGlobal(num);
        }
    }

    public static bool IsStarted(uint devInst)
    {
        uint pulStatus;
        uint pulProblemNumber;
        return CM_Get_DevNode_Status(out pulStatus, out pulProblemNumber, devInst, 0u) == 0 && (pulStatus & 8) != 0;
    }
}
