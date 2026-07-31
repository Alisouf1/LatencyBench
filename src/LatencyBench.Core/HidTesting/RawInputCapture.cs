using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.HidTesting;

public sealed class RawInputCapture
{
	private readonly List<long> _timestamps = new List<long>();

	private nint _targetDeviceHandle = IntPtr.Zero;

	private ushort _usagePage;

	private ushort _usage;

	private bool _capturing;

	public IReadOnlyList<long> Timestamps => _timestamps;

	public static double TicksPerMillisecond => (double)Stopwatch.Frequency / 1000.0;

	public static nint? FindDeviceHandle(string devicePath)
	{
		string b = StripInterfaceGuid(devicePath);
		foreach (nint item in RawInputApi.ListDeviceHandles())
		{
			string? deviceName = RawInputApi.GetDeviceName(item);
			if (deviceName != null && string.Equals(StripInterfaceGuid(deviceName), b, StringComparison.OrdinalIgnoreCase))
			{
				return item;
			}
		}
		return null;
	}

	private static string StripInterfaceGuid(string devicePath)
	{
		int num = devicePath.LastIndexOf('#');
		return (num > 0) ? devicePath.Substring(0, num) : devicePath;
	}

	public bool Start(nint windowHandle, string devicePath, ushort usagePage, ushort usage)
	{
		nint? num = FindDeviceHandle(devicePath);
		if (!num.HasValue)
		{
			return false;
		}
		_targetDeviceHandle = num.Value;
		_usagePage = usagePage;
		_usage = usage;
		_timestamps.Clear();
		RAWINPUTDEVICE rAWINPUTDEVICE = new RAWINPUTDEVICE
		{
			usUsagePage = usagePage,
			usUsage = usage,
			dwFlags = 256u,
			hwndTarget = windowHandle
		};
		_capturing = RawInputApi.RegisterRawInputDevices(new RAWINPUTDEVICE[1] { rAWINPUTDEVICE }, 1u, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
		return _capturing;
	}

	public void Stop()
	{
		if (_capturing)
		{
			RAWINPUTDEVICE rAWINPUTDEVICE = new RAWINPUTDEVICE
			{
				usUsagePage = _usagePage,
				usUsage = _usage,
				dwFlags = 1u,
				hwndTarget = IntPtr.Zero
			};
			RawInputApi.RegisterRawInputDevices(new RAWINPUTDEVICE[1] { rAWINPUTDEVICE }, 1u, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
			_capturing = false;
		}
	}

	public bool OnRawInputMessage(int msg, nint lParam)
	{
		if (!_capturing || msg != 255)
		{
			return false;
		}
		if (RawInputApi.GetDeviceHandleFromMessage(lParam) != _targetDeviceHandle)
		{
			return false;
		}
		_timestamps.Add(Stopwatch.GetTimestamp());
		return true;
	}
}
