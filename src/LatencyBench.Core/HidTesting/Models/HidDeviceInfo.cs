using System;

namespace LatencyBench.Core.HidTesting.Models;

public sealed class HidDeviceInfo
{
	public required string InstanceId { get; init; }

	public required string FriendlyName { get; init; }

	public required string DevicePath { get; init; }

	public string? DeviceClass { get; init; }

	public ushort UsagePage { get; init; }

	public ushort Usage { get; init; }

	public bool IsMouse => string.Equals(DeviceClass, "Mouse", StringComparison.OrdinalIgnoreCase) || (UsagePage == 1 && Usage == 2);

	public bool IsKeyboard => string.Equals(DeviceClass, "Keyboard", StringComparison.OrdinalIgnoreCase) || (UsagePage == 1 && Usage == 6);

	public (ushort UsagePage, ushort Usage) EffectiveUsage => ((ushort UsagePage, ushort Usage))(IsMouse ? (UsagePage: 1, Usage: 2) : (IsKeyboard ? (UsagePage: 1, Usage: 6) : (UsagePage: (int)UsagePage, Usage: (int)Usage)));
}
