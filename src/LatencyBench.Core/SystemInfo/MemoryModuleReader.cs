using System.Collections.Generic;
using System.Management;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Reads installed RAM modules via WMI.
/// <para>
/// This is the one place in detection besides <see cref="DeviceGuardReader"/> that pays WMI's cost:
/// there is no registry or Win32 API equivalent for a module's rated versus currently-configured
/// clock speed, which is the whole reason this reader exists — Win32_PhysicalMemory.Speed is the
/// rated maximum, ConfiguredClockSpeed is what it is actually clocked at right now, and the gap
/// between the two is exactly "an XMP/DOCP/EXPO profile exists but was never enabled in firmware".
/// </para>
/// </summary>
public sealed class MemoryModuleReader
{
	public IReadOnlyList<MemoryModuleInfo> Read()
	{
		var results = new List<MemoryModuleInfo>();

		using var searcher = new ManagementObjectSearcher(
			"SELECT BankLabel, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");

		using ManagementObjectCollection modules = searcher.Get();
		foreach (ManagementBaseObject module in modules)
		{
			using (module)
			{
				results.Add(new MemoryModuleInfo(
					BankLabel: Clean(module["BankLabel"] as string),
					Manufacturer: Clean(module["Manufacturer"] as string),
					PartNumber: Clean(module["PartNumber"] as string),
					CapacityBytes: ReadUInt64(module["Capacity"]),
					RatedSpeedMHz: ReadUInt32(module["Speed"]),
					ConfiguredSpeedMHz: ReadUInt32(module["ConfiguredClockSpeed"])));
			}
		}

		return results;
	}

	private static string? Clean(string? value)
	{
		string? trimmed = value?.Trim();
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Capacity comes back as a string on some WMI providers and a boxed ulong on others —
	/// both are handled rather than trusting one shape.</summary>
	private static ulong ReadUInt64(object? value) => value switch
	{
		ulong u => u,
		string s when ulong.TryParse(s, out ulong parsed) => parsed,
		_ => 0
	};

	private static uint ReadUInt32(object? value) => value switch
	{
		uint u => u,
		string s when uint.TryParse(s, out uint parsed) => parsed,
		_ => 0
	};
}
