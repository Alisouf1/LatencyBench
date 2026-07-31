using LatencyBench.Core.SystemInfo.Models;
using Microsoft.Win32;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Board and firmware identity. Windows copies these out of SMBIOS into the registry at boot, so
/// reading them costs a key open rather than the WMI round trip Win32_BaseBoard and Win32_BIOS need.
/// </summary>
public sealed class MotherboardReader
{
	private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

	public MotherboardInfo Read()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BiosKey);

		return new MotherboardInfo
		{
			Manufacturer = Clean(key?.GetValue("BaseBoardManufacturer") as string),
			Product = Clean(key?.GetValue("BaseBoardProduct") as string),
			SystemManufacturer = Clean(key?.GetValue("SystemManufacturer") as string),
			SystemProductName = Clean(key?.GetValue("SystemProductName") as string),
			BiosVendor = Clean(key?.GetValue("BIOSVendor") as string),
			BiosVersion = Clean(key?.GetValue("BIOSVersion") as string),
			BiosReleaseDate = Clean(key?.GetValue("BIOSReleaseDate") as string)
		};
	}

	/// <summary>
	/// OEMs frequently leave SMBIOS placeholders in place. Treating those as absent stops the UI
	/// showing a user "Motherboard: To Be Filled By O.E.M." as if it were real information.
	/// </summary>
	private static string? Clean(string? value)
	{
		string? trimmed = value?.Trim();
		if (string.IsNullOrEmpty(trimmed))
		{
			return null;
		}

		string[] placeholders =
		{
			"To Be Filled By O.E.M.",
			"To be filled by O.E.M.",
			"Default string",
			"System Product Name",
			"System manufacturer",
			"Not Applicable",
			"None",
			"N/A",
			"Unknown"
		};

		foreach (string placeholder in placeholders)
		{
			if (string.Equals(trimmed, placeholder, System.StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
		}

		return trimmed;
	}
}
