using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LatencyBench.Core.Interop;

internal static class RawInputApi
{
	public const int WM_INPUT = 255;

	public const uint RIDEV_REMOVE = 1u;

	public const uint RIDEV_INPUTSINK = 256u;

	public const uint RIDI_DEVICENAME = 536870919u;

	public const uint RID_HEADER = 268435461u;

	public const uint RID_INPUT = 268435459u;

	public const uint RIM_TYPEMOUSE = 0u;

	public const ushort UsagePageGeneric = 1;

	public const ushort UsageMouse = 2;

	public const ushort UsageKeyboard = 6;

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern uint GetRawInputDeviceInfoW(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern uint GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

	public static string? GetDeviceName(nint hDevice)
	{
		uint pcbSize = 0u;
		GetRawInputDeviceInfoW(hDevice, 536870919u, IntPtr.Zero, ref pcbSize);
		if (pcbSize == 0)
		{
			return null;
		}
		nint num = Marshal.AllocHGlobal((int)(pcbSize * 2));
		try
		{
			uint rawInputDeviceInfoW = GetRawInputDeviceInfoW(hDevice, 536870919u, num, ref pcbSize);
			return (rawInputDeviceInfoW == uint.MaxValue) ? null : Marshal.PtrToStringUni(num);
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}

	public static List<nint> ListDeviceHandles()
	{
		uint puiNumDevices = 0u;
		GetRawInputDeviceList(null, ref puiNumDevices, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
		if (puiNumDevices == 0)
		{
			return new List<nint>();
		}
		RAWINPUTDEVICELIST[] array = new RAWINPUTDEVICELIST[puiNumDevices];
		uint rawInputDeviceList = GetRawInputDeviceList(array, ref puiNumDevices, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
		return (rawInputDeviceList == uint.MaxValue) ? new List<nint>() : (from d in array.Take((int)rawInputDeviceList)
			select d.hDevice).ToList();
	}

	public static nint? GetDeviceHandleFromMessage(nint lParam)
	{
		uint pcbSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
		nint num = Marshal.AllocHGlobal((int)pcbSize);
		try
		{
			uint rawInputData = GetRawInputData(lParam, 268435461u, num, ref pcbSize, pcbSize);
			if (rawInputData == uint.MaxValue)
			{
				return null;
			}
			return Marshal.PtrToStructure<RAWINPUTHEADER>(num).hDevice;
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}

	public static bool TryReadMouseDelta(nint lParam, out RawMouseDelta delta)
	{
		delta = default(RawMouseDelta);
		uint pcbSize = 0u;
		GetRawInputData(lParam, 268435459u, IntPtr.Zero, ref pcbSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
		if (pcbSize == 0)
		{
			return false;
		}
		nint num = Marshal.AllocHGlobal((int)pcbSize);
		try
		{
			uint rawInputData = GetRawInputData(lParam, 268435459u, num, ref pcbSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
			if (rawInputData == uint.MaxValue)
			{
				return false;
			}
			if (Marshal.PtrToStructure<RAWINPUTHEADER>(num).dwType != 0)
			{
				return false;
			}
			nint ptr = IntPtr.Add(num, Marshal.SizeOf<RAWINPUTHEADER>());
			RAWMOUSE rAWMOUSE = Marshal.PtrToStructure<RAWMOUSE>(ptr);
			delta = new RawMouseDelta(rAWMOUSE.lLastX, rAWMOUSE.lLastY, rAWMOUSE.usButtonFlags);
			return true;
		}
		finally
		{
			Marshal.FreeHGlobal(num);
		}
	}
}
