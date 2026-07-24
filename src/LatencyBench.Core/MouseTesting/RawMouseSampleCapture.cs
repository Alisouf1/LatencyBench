using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LatencyBench.Core.HidTesting;
using LatencyBench.Core.Interop;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Core.MouseTesting;

public sealed class RawMouseSampleCapture
{
	private List<MouseSample> _samples = new List<MouseSample>();

	private nint _targetDeviceHandle = IntPtr.Zero;

	private ushort _usagePage;

	private ushort _usage;

	private bool _capturing;

	public IReadOnlyList<MouseSample> Samples => _samples;

	public static double TicksPerMillisecond => (double)Stopwatch.Frequency / 1000.0;

	public bool Start(nint windowHandle, string devicePath, ushort usagePage, ushort usage, int capacityHint = 4096)
	{
		nint? num = RawInputCapture.FindDeviceHandle(devicePath);
		if (!num.HasValue)
		{
			return false;
		}
		_targetDeviceHandle = num.Value;
		_usagePage = usagePage;
		_usage = usage;
		_samples = new List<MouseSample>(Math.Max(capacityHint, 16));
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
		if (!RawInputApi.TryReadMouseDelta(lParam, out var delta))
		{
			return false;
		}
		_samples.Add(new MouseSample(Stopwatch.GetTimestamp(), delta.Dx, delta.Dy, delta.ButtonFlags));
		return true;
	}
}
