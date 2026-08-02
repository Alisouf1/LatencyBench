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

    /// <summary>
    /// The report arrival times to analyse. When a combo device exposes both a mouse and a keyboard
    /// collection, both are merged in timestamp order.
    /// <para>
    /// This used to return whichever of the two collections had more entries and throw the other away,
    /// which contradicted <see cref="OnRawInputMessage"/> — that method goes out of its way to capture
    /// both collections precisely so neither half is dropped. On a combo device the discarded
    /// collection's reports were missing from the interval series, which inflated every gap that
    /// spanned them and reported a polling rate below what the device was actually achieving.
    /// </para>
    /// </summary>
    private IReadOnlyList<long> Timestamps
    {
        get
        {
            IReadOnlyList<long> keyboard = _keyboardActive ? _keyboardCapture.Timestamps : Array.Empty<long>();
            IReadOnlyList<long> mouse = _mouseActive ? _rawInputCapture.Timestamps : Array.Empty<long>();

            if (keyboard.Count == 0)
            {
                return mouse;
            }

            if (mouse.Count == 0)
            {
                return keyboard;
            }

            // Each source is already in arrival order, so this is a linear merge, not a sort.
            var merged = new List<long>(keyboard.Count + mouse.Count);
            int keyboardIndex = 0;
            int mouseIndex = 0;
            while (keyboardIndex < keyboard.Count && mouseIndex < mouse.Count)
            {
                merged.Add(keyboard[keyboardIndex] <= mouse[mouseIndex]
                    ? keyboard[keyboardIndex++]
                    : mouse[mouseIndex++]);
            }

            while (keyboardIndex < keyboard.Count)
            {
                merged.Add(keyboard[keyboardIndex++]);
            }

            while (mouseIndex < mouse.Count)
            {
                merged.Add(mouse[mouseIndex++]);
            }

            return merged;
        }
    }

    /// <summary>Counted from the sources directly — going through <see cref="Timestamps"/> would build
    /// the merged list just to read its length, and this is polled by the progress timer.</summary>
    public int CurrentSampleCount =>
        (_keyboardActive ? _keyboardCapture.Timestamps.Count : 0)
        + (_mouseActive ? _rawInputCapture.Timestamps.Count : 0);

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
        // Analyze only reads the sequence, so the defensive ToList() copy this used to make was pure
        // overhead — and it ran on every live refresh, over a list that reaches tens of thousands of
        // entries on a high-polling-rate device.
        return JitterLatencyAnalyzer.Analyze(Timestamps, RawInputCapture.TicksPerMillisecond);
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
        JitterLatencyAnalyzer.AnalysisResult? analysisResult = JitterLatencyAnalyzer.Analyze(Timestamps, RawInputCapture.TicksPerMillisecond);
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
            MedianReportIntervalMs = analysisResult.Value.MedianReportIntervalMs,
            EffectivePollingRateHz = analysisResult.Value.EffectivePollingRateHz,
            LateReportPercent = analysisResult.Value.LateReportPercent,
            ActiveIntervalsMs = analysisResult.Value.ActiveIntervalsMs
        };
    }
}
