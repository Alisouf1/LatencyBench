using System;

namespace LatencyBench.Core.SystemInfo.Models;

/// <summary>A Windows feature that is either off, on, or genuinely not determinable.</summary>
public enum FeatureState
{
    Unknown,
    Disabled,
    Enabled
}

/// <summary>
/// The Windows build, and the features whose state changes whether a given optimisation is
/// applicable, effective, or actively unsafe.
/// </summary>
public sealed class WindowsInfo
{
    public required string ProductName { get; init; }

    /// <summary>The marketing version — "24H2", "23H2". Absent on builds before Windows 10 2004.</summary>
    public string? DisplayVersion { get; init; }

    public required int MajorVersion { get; init; }

    public required int MinorVersion { get; init; }

    public required int BuildNumber { get; init; }

    /// <summary>Update Build Revision — the part after the build number in "26100.2314".</summary>
    public int UpdateBuildRevision { get; init; }

    public string? EditionId { get; init; }

    /// <summary>True for Windows 11, which is Windows 10 major/minor with a build of 22000 or above.
    /// There is no separate major version, so the build number is the only reliable test.</summary>
    public bool IsWindows11 => MajorVersion == 10 && BuildNumber >= 22000;

    public bool IsWindows10 => MajorVersion == 10 && BuildNumber < 22000;

    /// <summary>Hardware-accelerated GPU scheduling. Requires a reboot to change, so the registry
    /// value is the configured state and not necessarily what the running system is doing.</summary>
    public required FeatureState HardwareAcceleratedGpuScheduling { get; init; }

    /// <summary>True when the display driver reports HAGS as supported at all. Setting HwSchMode on a
    /// GPU or driver that does not support it does nothing.</summary>
    public required bool SupportsHardwareAcceleratedGpuScheduling { get; init; }

    /// <summary>Virtualisation-based security. When enabled it adds a hypervisor layer under the OS
    /// that measurably raises interrupt and DPC latency, and it makes some timer behaviour differ.</summary>
    public required FeatureState VirtualizationBasedSecurity { get; init; }

    /// <summary>Memory integrity / hypervisor-enforced code integrity — the "Core isolation" switch
    /// in Windows Security, and the single largest latency cost among the security features.</summary>
    public required FeatureState MemoryIntegrity { get; init; }

    public required FeatureState GameMode { get; init; }

    /// <summary>
    /// True when the platform uses modern standby (S0 low-power idle) rather than classic S3 sleep.
    /// On these systems Windows hides the High performance power plan and drives processor
    /// performance through the Power mode slider instead.
    /// </summary>
    public required bool UsesModernStandby { get; init; }

    /// <summary>True when the running OS is itself inside a virtual machine, where interrupt affinity,
    /// MSI mode and DPC measurements describe the host's virtual devices rather than real hardware.</summary>
    public required bool IsVirtualMachine { get; init; }

    public string VersionString => UpdateBuildRevision > 0
        ? $"{MajorVersion}.{MinorVersion}.{BuildNumber}.{UpdateBuildRevision}"
        : $"{MajorVersion}.{MinorVersion}.{BuildNumber}";
}
