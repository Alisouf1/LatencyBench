using LatencyBench.Core.HidTesting.Models;

namespace LatencyBench.App.ViewModels;

/// <summary>A mouse-capable HID device offered in the Mouse test device picker.</summary>
public sealed record MouseDeviceOption(HidDeviceInfo Device, string DisplayLabel);
