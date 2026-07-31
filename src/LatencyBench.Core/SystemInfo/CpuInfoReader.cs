using System;
using LatencyBench.Core.SystemInfo.Models;
using Microsoft.Win32;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// CPU identity, from the firmware-populated registry description rather than WMI's
/// Win32_Processor. Win32_Processor is one of the slowest classes in WMI — it can take several
/// hundred milliseconds on first access because querying it makes Windows re-evaluate processor
/// power information — and it returns the same strings this key already holds.
/// </summary>
public sealed class CpuInfoReader
{
	private const string CentralProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

	private readonly CpuTopologyReader _topologyReader;

	public CpuInfoReader(CpuTopologyReader? topologyReader = null)
	{
		_topologyReader = topologyReader ?? new CpuTopologyReader();
	}

	public CpuInfo Read()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CentralProcessorKey);

		string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown processor";
		string vendorIdentifier = (key?.GetValue("VendorIdentifier") as string)?.Trim() ?? string.Empty;
		int nominalMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;

		return new CpuInfo
		{
			Name = name,
			Vendor = ClassifyVendor(vendorIdentifier, name),
			VendorIdentifier = vendorIdentifier,
			NominalMhz = nominalMhz,
			Topology = _topologyReader.Read()
		};
	}

	private static CpuVendor ClassifyVendor(string vendorIdentifier, string name)
	{
		// VendorIdentifier is the CPUID vendor string and is the reliable source; the marketing name
		// is only consulted when firmware left the identifier empty, which happens in some VMs.
		if (vendorIdentifier.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
		{
			return CpuVendor.Intel;
		}

		if (vendorIdentifier.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
		{
			return CpuVendor.Amd;
		}

		return CpuVendor.Unknown;
	}
}
