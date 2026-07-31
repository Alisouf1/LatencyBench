using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LatencyBench.Core.SystemInfo.Models;
using Microsoft.Win32;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Display adapters, with the driver version and memory size taken from the adapter's own class key.
/// <para>
/// Win32_VideoController would return the same information, but it reports
/// <c>AdapterRAM</c> as a signed 32-bit value: any GPU with 4 GB or more of memory comes back
/// wrong, and cards at exactly 4 GB come back negative. The class key holds the real 64-bit value.
/// </para>
/// </summary>
public sealed class GpuEnumerator
{
	private const string ClassKeyRoot = @"SYSTEM\CurrentControlSet\Control\Class";

	public IReadOnlyList<GpuInfo> Enumerate()
	{
		var devices = DeviceClassEnumerator.Enumerate(DeviceClassEnumerator.DisplayClass);
		var results = new List<GpuInfo>(devices.Count);

		foreach (var device in devices)
		{
			using RegistryKey? classKey = device.DriverKeyPath is null
				? null
				: Registry.LocalMachine.OpenSubKey($@"{ClassKeyRoot}\{device.DriverKeyPath}");

			results.Add(new GpuInfo
			{
				Name = device.FriendlyName,
				InstanceId = device.InstanceId,
				DriverVersion = classKey?.GetValue("DriverVersion") as string,
				DriverDate = ParseDriverDate(classKey?.GetValue("DriverDate") as string),
				Vendor = ClassifyVendor(device.FriendlyName),
				DedicatedVideoMemoryBytes = ReadMemorySize(classKey),
				IsPrimary = false
			});
		}

		return MarkPrimary(results);
	}

	/// <summary>
	/// "Primary" is decided by the largest dedicated memory rather than by asking the display
	/// subsystem, because on a laptop with switchable graphics the adapter driving the desktop
	/// changes with what is running, while the adapter the user cares about tuning does not.
	/// </summary>
	private static IReadOnlyList<GpuInfo> MarkPrimary(List<GpuInfo> gpus)
	{
		if (gpus.Count == 0)
		{
			return gpus;
		}

		GpuInfo best = gpus
			.OrderByDescending(gpu => gpu.DedicatedVideoMemoryBytes ?? 0UL)
			.ThenBy(gpu => gpu.Name, StringComparer.OrdinalIgnoreCase)
			.First();

		return gpus
			.Select(gpu => ReferenceEquals(gpu, best) ? Clone(gpu, isPrimary: true) : gpu)
			.ToList();
	}

	private static GpuInfo Clone(GpuInfo source, bool isPrimary) => new()
	{
		Name = source.Name,
		InstanceId = source.InstanceId,
		DriverVersion = source.DriverVersion,
		DriverDate = source.DriverDate,
		Vendor = source.Vendor,
		DedicatedVideoMemoryBytes = source.DedicatedVideoMemoryBytes,
		IsPrimary = isPrimary
	};

	private static ulong? ReadMemorySize(RegistryKey? classKey)
	{
		// Modern drivers write a QWORD; older ones wrote a DWORD under the same name. The DWORD form
		// comes back from the registry API as an int, which is why both cases are handled.
		object? raw = classKey?.GetValue("HardwareInformation.qwMemorySize");
		return raw switch
		{
			long value when value > 0 => (ulong)value,
			int value when value > 0 => (ulong)value,
			byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
			byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
			_ => null
		};
	}

	private static DateTime? ParseDriverDate(string? value)
	{
		// Windows stores this as M-D-YYYY, invariant, regardless of the machine's locale.
		return DateTime.TryParseExact(value, "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
			? parsed
			: null;
	}

	private static string? ClassifyVendor(string name)
	{
		if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
		{
			return "NVIDIA";
		}

		if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
		{
			return "AMD";
		}

		if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
		{
			return "Intel";
		}

		return null;
	}
}
