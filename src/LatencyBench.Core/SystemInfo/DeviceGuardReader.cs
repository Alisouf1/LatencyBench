using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// The running state of virtualisation-based security and memory integrity.
/// <para>
/// This is the one detection that justifies a WMI query. The DeviceGuard registry values only
/// describe what policy asked for — they are absent entirely on a machine where Windows enabled
/// VBS by OEM default, which is the common case on a modern preinstall — so reading them alone
/// reports "Unknown" on exactly the machines where the answer matters most. VBS puts a hypervisor
/// underneath the kernel and is among the largest single contributors to interrupt and DPC latency,
/// so the recommendation engine cannot be honest without knowing whether it is actually running.
/// </para>
/// </summary>
public sealed class DeviceGuardReader
{
    private const string Namespace = @"root\Microsoft\Windows\DeviceGuard";

    /// <summary>Win32_DeviceGuard.SecurityServicesRunning value for hypervisor-enforced code
    /// integrity — the "Memory integrity" switch in Windows Security.</summary>
    private const uint SecurityServiceHypervisorEnforcedCodeIntegrity = 2;

    /// <summary>VirtualizationBasedSecurityStatus: 0 off, 1 enabled but not running, 2 running.</summary>
    private const uint VbsRunning = 2;

    public (FeatureState VirtualizationBasedSecurity, FeatureState MemoryIntegrity) Read()
    {
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(Namespace),
            new ObjectQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"));

        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                FeatureState vbs = result["VirtualizationBasedSecurityStatus"] is uint status
                    ? (status == VbsRunning ? FeatureState.Enabled : FeatureState.Disabled)
                    : FeatureState.Unknown;

                FeatureState hvci = ReadMemoryIntegrity(result["SecurityServicesRunning"]);

                return (vbs, hvci);
            }
        }

        // The class exists on every supported Windows but returns no instance on editions where
        // Device Guard is unavailable.
        return (FeatureState.Unknown, FeatureState.Unknown);
    }

    private static FeatureState ReadMemoryIntegrity(object? securityServicesRunning)
    {
        if (securityServicesRunning is not uint[] services)
        {
            return FeatureState.Unknown;
        }

        // An empty array is a definite answer — VBS reported its running services and memory
        // integrity was not among them.
        return services.Contains(SecurityServiceHypervisorEnforcedCodeIntegrity)
            ? FeatureState.Enabled
            : FeatureState.Disabled;
    }
}
