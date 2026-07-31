using System;
using System.Runtime.InteropServices;

namespace LatencyBench.Core.Interop;

/// <summary>
/// Topology, memory and OS-version entry points used by the hardware detection layer.
/// </summary>
internal static class SystemInfoApi
{
	internal const int ErrorInsufficientBuffer = 122;

	/// <summary>LOGICAL_PROCESSOR_RELATIONSHIP.RelationAll</summary>
	internal const uint RelationAll = 0xFFFF;

	internal const uint RelationProcessorCore = 0;
	internal const uint RelationNumaNode = 1;
	internal const uint RelationCache = 2;
	internal const uint RelationProcessorPackage = 3;
	internal const uint RelationGroup = 4;

	/// <summary>PROCESSOR_RELATIONSHIP.Flags — the core has more than one logical processor.</summary>
	internal const byte LtpPcSmt = 0x1;

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetLogicalProcessorInformationEx(
		uint relationshipType,
		byte[]? buffer,
		ref uint returnedLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

	[DllImport("kernel32.dll")]
	internal static extern ushort GetActiveProcessorGroupCount();

	[DllImport("kernel32.dll")]
	internal static extern uint GetActiveProcessorCount(ushort groupNumber);

	internal const ushort AllProcessorGroups = 0xFFFF;

	/// <summary>
	/// The only version API that is not subject to the compatibility shims that make
	/// GetVersionEx report Windows 8 to an unmanifested caller. Documented as kernel-mode but
	/// exported from ntdll for user mode and used exactly this way throughout Windows itself.
	/// </summary>
	[DllImport("ntdll.dll")]
	internal static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW versionInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

	/// <summary>SYSTEM_POWER_STATUS.BatteryFlag — the machine has no system battery.</summary>
	internal const byte BatteryFlagNoSystemBattery = 128;

	/// <summary>SYSTEM_POWER_STATUS.BatteryFlag — status could not be determined.</summary>
	internal const byte BatteryFlagUnknown = 255;

	[StructLayout(LayoutKind.Sequential)]
	internal struct SYSTEM_POWER_STATUS
	{
		internal byte ACLineStatus;
		internal byte BatteryFlag;
		internal byte BatteryLifePercent;
		internal byte SystemStatusFlag;
		internal uint BatteryLifeTime;
		internal uint BatteryFullLifeTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MEMORYSTATUSEX
	{
		internal uint dwLength;
		internal uint dwMemoryLoad;
		internal ulong ullTotalPhys;
		internal ulong ullAvailPhys;
		internal ulong ullTotalPageFile;
		internal ulong ullAvailPageFile;
		internal ulong ullTotalVirtual;
		internal ulong ullAvailVirtual;
		internal ulong ullAvailExtendedVirtual;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct RTL_OSVERSIONINFOEXW
	{
		internal uint dwOSVersionInfoSize;
		internal uint dwMajorVersion;
		internal uint dwMinorVersion;
		internal uint dwBuildNumber;
		internal uint dwPlatformId;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		internal string szCSDVersion;

		internal ushort wServicePackMajor;
		internal ushort wServicePackMinor;
		internal ushort wSuiteMask;
		internal byte wProductType;
		internal byte wReserved;
	}
}
