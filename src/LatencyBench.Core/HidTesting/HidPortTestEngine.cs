using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.HidTesting.Models;

namespace LatencyBench.Core.HidTesting;

public sealed class HidPortTestEngine
{
	private readonly RawInputCapture _rawInputCapture = new RawInputCapture();

	/// <summary>
	/// Keyboards are captured through Raw Input, exactly like mice — NOT through a WH_KEYBOARD_LL hook.
	/// A low-level keyboard hook routes every keystroke on the entire machine through this process's UI
	/// thread and gives it only LowLevelHooksTimeout (300 ms by default) to respond; during a port test
	/// that thread is also driving the progress loop, an optional ETW trace, and — with "test under
	/// load" — a deliberate CPU saturation, so keystrokes system-wide stalled or dropped and the
	/// keyboard appeared frozen. Raw Input is passive: it observes input without sitting in the delivery
	/// path of other applications, so it cannot freeze anything. It also filters by device handle, which
	/// makes the measurement per-device instead of counting every keyboard on the system.
	/// </summary>
	private readonly RawInputCapture _keyboardCapture = new RawInputCapture();

	private bool _mouseActive;

	private bool _keyboardActive;

	private DateTime _captureStartTime;

	private long _captureStartTicks;

	public bool IsCapturing { get; private set; }

	private IReadOnlyList<long> Timestamps
	{
		get
		{
			IReadOnlyList<long> readOnlyList2;
			if (!_keyboardActive)
			{
				IReadOnlyList<long> readOnlyList = Array.Empty<long>();
				readOnlyList2 = readOnlyList;
			}
			else
			{
				readOnlyList2 = _keyboardCapture.Timestamps;
			}
			IReadOnlyList<long> readOnlyList3 = readOnlyList2;
			IReadOnlyList<long> readOnlyList4;
			if (!_mouseActive)
			{
				IReadOnlyList<long> readOnlyList = Array.Empty<long>();
				readOnlyList4 = readOnlyList;
			}
			else
			{
				readOnlyList4 = _rawInputCapture.Timestamps;
			}
			IReadOnlyList<long> readOnlyList5 = readOnlyList4;
			return (readOnlyList3.Count >= readOnlyList5.Count) ? readOnlyList3 : readOnlyList5;
		}
	}

	public int CurrentSampleCount => Timestamps.Count;

	public IReadOnlyList<double> GetRecentIntervalsMs(int maxCount)
	{
		IReadOnlyList<long> timestamps = Timestamps;
		if (timestamps.Count < 2)
		{
			return Array.Empty<double>();
		}
		int num = Math.Max(1, timestamps.Count - maxCount);
		List<double> list = new List<double>();
		for (int i = num; i < timestamps.Count; i++)
		{
			list.Add((double)(timestamps[i] - timestamps[i - 1]) / RawInputCapture.TicksPerMillisecond);
		}
		return list;
	}

	public JitterLatencyAnalyzer.AnalysisResult? GetCurrentAnalysis()
	{
		return JitterLatencyAnalyzer.Analyze(Timestamps.ToList(), RawInputCapture.TicksPerMillisecond);
	}

	public IReadOnlyList<ReportWindow> GetReportWindows(double medianMs)
	{
		IReadOnlyList<long> timestamps = Timestamps;
		List<ReportWindow> list = new List<ReportWindow>();
		if (medianMs <= 0.0)
		{
			return list;
		}
		for (int i = 1; i < timestamps.Count; i++)
		{
			double num = (double)(timestamps[i] - timestamps[i - 1]) / RawInputCapture.TicksPerMillisecond;
			if (!(num > medianMs * 10.0))
			{
				list.Add(new ReportWindow(ToWallClock(timestamps[i - 1]), ToWallClock(timestamps[i]), num > medianMs * 1.5));
			}
		}
		return list;
	}

	private DateTime ToWallClock(long stopwatchTicks)
	{
		return _captureStartTime.AddSeconds((double)(stopwatchTicks - _captureStartTicks) / (double)Stopwatch.Frequency);
	}

	public bool StartCapture(nint windowHandle, HidDeviceInfo device, HidDeviceInfo? mouseCollection = null, HidDeviceInfo? keyboardCollection = null)
	{
		_captureStartTime = DateTime.Now;
		_captureStartTicks = Stopwatch.GetTimestamp();
		if (mouseCollection == null)
		{
			mouseCollection = (device.IsMouse ? device : null);
		}
		if (keyboardCollection == null)
		{
			keyboardCollection = (device.IsKeyboard ? device : null);
		}
		if (mouseCollection == null && keyboardCollection == null)
		{
			mouseCollection = device;
		}
		_mouseActive = false;
		_keyboardActive = false;
		if (keyboardCollection != null)
		{
			(ushort UsagePage, ushort Usage) keyboardUsage = keyboardCollection.EffectiveUsage;
			_keyboardActive = _keyboardCapture.Start(windowHandle, keyboardCollection.DevicePath, keyboardUsage.UsagePage, keyboardUsage.Usage);
		}
		if (mouseCollection != null)
		{
			(ushort UsagePage, ushort Usage) effectiveUsage = mouseCollection.EffectiveUsage;
			ushort item = effectiveUsage.UsagePage;
			ushort item2 = effectiveUsage.Usage;
			_mouseActive = _rawInputCapture.Start(windowHandle, mouseCollection.DevicePath, item, item2);
		}
		IsCapturing = _mouseActive || _keyboardActive;
		return IsCapturing;
	}

	public bool OnRawInputMessage(int msg, nint lParam)
	{
		// Both are evaluated into locals first, deliberately avoiding short-circuit: a combo device can
		// expose a mouse and a keyboard collection at once, and skipping either would silently drop half
		// the samples.
		bool keyboardHandled = _keyboardActive && _keyboardCapture.OnRawInputMessage(msg, lParam);
		bool mouseHandled = _mouseActive && _rawInputCapture.OnRawInputMessage(msg, lParam);
		return keyboardHandled || mouseHandled;
	}

	public void EnsureStopped()
	{
		if (IsCapturing)
		{
			if (_keyboardActive)
			{
				_keyboardCapture.Stop();
			}
			if (_mouseActive)
			{
				_rawInputCapture.Stop();
			}
			IsCapturing = false;
		}
	}

	public HidTestResult? StopCaptureAndAnalyze(string deviceFriendlyName, string portLabel, string portLocation)
	{
		if (_keyboardActive)
		{
			_keyboardCapture.Stop();
		}
		if (_mouseActive)
		{
			_rawInputCapture.Stop();
		}
		IsCapturing = false;
		JitterLatencyAnalyzer.AnalysisResult? analysisResult = JitterLatencyAnalyzer.Analyze(Timestamps.ToList(), RawInputCapture.TicksPerMillisecond);
		if (!analysisResult.HasValue)
		{
			return null;
		}
		return new HidTestResult
		{
			DeviceFriendlyName = deviceFriendlyName,
			PortLabel = portLabel,
			PortLocation = portLocation,
			SampleCount = analysisResult.Value.SampleCount,
			PollingRateHz = analysisResult.Value.PollingRateHz,
			JitterMs = analysisResult.Value.JitterMs,
			ReportLatencyMs = analysisResult.Value.ReportLatencyMs,
			EffectivePollingRateHz = analysisResult.Value.EffectivePollingRateHz,
			LateReportPercent = analysisResult.Value.LateReportPercent,
			ActiveIntervalsMs = analysisResult.Value.ActiveIntervalsMs
		};
	}
}
