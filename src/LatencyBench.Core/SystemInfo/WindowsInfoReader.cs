using System;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;
using LatencyBench.Core.SystemInfo.Models;
using Microsoft.Win32;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Detects the Windows build and the feature states that decide whether an optimisation applies.
/// Everything here is a registry read or a single ntdll call — no WMI, because this runs during
/// startup and a WMI connection alone costs more than every read in this class combined.
/// </summary>
public sealed class WindowsInfoReader
{
	private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

	private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

	private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";

	private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

	private const string GameBarKey = @"Software\Microsoft\GameBar";

	private const string SystemBiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

	public WindowsInfo Read()
	{
		var version = new SystemInfoApi.RTL_OSVERSIONINFOEXW
		{
			dwOSVersionInfoSize = (uint)Marshal.SizeOf<SystemInfoApi.RTL_OSVERSIONINFOEXW>(),
			szCSDVersion = string.Empty
		};

		// GetVersionEx lies to any process without a matching compatibility manifest entry — it caps
		// at 6.2 (Windows 8). RtlGetVersion is not shimmed and reports the real build.
		int status = SystemInfoApi.RtlGetVersion(ref version);
		bool versionValid = status == 0;

		using RegistryKey? currentVersion = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);

		int build = versionValid && version.dwBuildNumber > 0
			? (int)version.dwBuildNumber
			: ReadInt32String(currentVersion, "CurrentBuild") ?? 0;

		return new WindowsInfo
		{
			ProductName = ResolveProductName(currentVersion, build),
			DisplayVersion = currentVersion?.GetValue("DisplayVersion") as string,
			MajorVersion = versionValid ? (int)version.dwMajorVersion : 10,
			MinorVersion = versionValid ? (int)version.dwMinorVersion : 0,
			BuildNumber = build,
			UpdateBuildRevision = currentVersion?.GetValue("UBR") is int ubr ? ubr : 0,
			EditionId = currentVersion?.GetValue("EditionID") as string,
			HardwareAcceleratedGpuScheduling = ReadHardwareAcceleratedGpuScheduling(),
			SupportsHardwareAcceleratedGpuScheduling = ReadHardwareSchedulingSupported(),
			VirtualizationBasedSecurity = ReadVirtualizationBasedSecurity(),
			MemoryIntegrity = ReadMemoryIntegrity(),
			GameMode = ReadGameMode(),
			UsesModernStandby = ReadModernStandby(),
			IsVirtualMachine = ReadIsVirtualMachine()
		};
	}

	/// <summary>
	/// ProductName in the registry still reads "Windows 10 …" on Windows 11 — Microsoft never updated
	/// it, so trusting it directly mislabels every Windows 11 machine.
	/// </summary>
	private static string ResolveProductName(RegistryKey? currentVersion, int build)
	{
		string raw = currentVersion?.GetValue("ProductName") as string ?? "Windows";
		if (build >= 22000 && raw.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
		{
			return raw.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
		}

		return raw;
	}

	private static FeatureState ReadHardwareAcceleratedGpuScheduling()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKey);
		return key?.GetValue("HwSchMode") switch
		{
			2 => FeatureState.Enabled,
			1 => FeatureState.Disabled,
			_ => FeatureState.Unknown
		};
	}

	/// <summary>
	/// HwSchMode can be set on any machine; it only does something when the display driver opts in.
	/// Windows records that separately, which is the difference between "off" and "not available".
	/// <para>
	/// HwSchSupported is not written by every driver — this machine reports HAGS enabled with the
	/// value absent entirely — so an enabled HwSchMode is treated as proof of support. Reporting
	/// "enabled but unsupported" would be self-contradictory, and it is the combination that would
	/// otherwise make the recommendation engine suggest turning on something already on.
	/// </para>
	/// </summary>
	private static bool ReadHardwareSchedulingSupported()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKey);
		if (key?.GetValue("HwSchSupported") is int supported)
		{
			return supported == 1;
		}

		return key?.GetValue("HwSchMode") is int mode && mode == 2;
	}

	private static FeatureState ReadVirtualizationBasedSecurity()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(DeviceGuardKey);
		if (key is null)
		{
			return FeatureState.Unknown;
		}

		return key.GetValue("EnableVirtualizationBasedSecurity") switch
		{
			1 => FeatureState.Enabled,
			0 => FeatureState.Disabled,
			_ => FeatureState.Unknown
		};
	}

	private static FeatureState ReadMemoryIntegrity()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
			$@"{DeviceGuardKey}\Scenarios\HypervisorEnforcedCodeIntegrity");
		if (key is null)
		{
			return FeatureState.Unknown;
		}

		return key.GetValue("Enabled") switch
		{
			1 => FeatureState.Enabled,
			0 => FeatureState.Disabled,
			_ => FeatureState.Unknown
		};
	}

	private static FeatureState ReadGameMode()
	{
		// Per-user setting, so it lives in HKCU and is absent until the user opens the Game Bar
		// settings page at least once. Absent means Windows' own default, which is on.
		using RegistryKey? key = Registry.CurrentUser.OpenSubKey(GameBarKey);
		if (key is null)
		{
			return FeatureState.Unknown;
		}

		return key.GetValue("AutoGameModeEnabled") switch
		{
			1 => FeatureState.Enabled,
			0 => FeatureState.Disabled,
			_ => FeatureState.Enabled
		};
	}

	/// <summary>
	/// CsEnabled is the flag Windows itself uses to decide whether the platform is a connected-standby
	/// (S0 low-power idle) system. On those machines the High performance power plan is hidden.
	/// </summary>
	private static bool ReadModernStandby()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PowerKey);
		return key?.GetValue("CsEnabled") is int enabled && enabled == 1;
	}

	/// <summary>
	/// Detected from firmware identity strings rather than a dedicated API, because the goal is only
	/// to warn the user that hardware-level tuning is meaningless in a guest — not to enumerate which
	/// hypervisor. A false negative just means no warning; a false positive would be worse, so the
	/// match is against vendor names that never appear on physical boards.
	/// </summary>
	private static bool ReadIsVirtualMachine()
	{
		using RegistryKey? bios = Registry.LocalMachine.OpenSubKey(SystemBiosKey);
		if (bios is null)
		{
			return false;
		}

		string haystack = string.Join(
			' ',
			bios.GetValue("SystemManufacturer") as string ?? string.Empty,
			bios.GetValue("SystemProductName") as string ?? string.Empty,
			bios.GetValue("BIOSVendor") as string ?? string.Empty);

		string[] markers =
		{
			"VMware", "VirtualBox", "innotek", "QEMU", "Xen", "KVM",
			"Parallels", "Virtual Machine", "Hyper-V", "Bochs"
		};

		foreach (string marker in markers)
		{
			if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>CurrentBuild is a REG_SZ, not a DWORD — reading it as an int always fails.</summary>
	private static int? ReadInt32String(RegistryKey? key, string valueName)
	{
		return key?.GetValue(valueName) is string text && int.TryParse(text, out int value) ? value : null;
	}
}
