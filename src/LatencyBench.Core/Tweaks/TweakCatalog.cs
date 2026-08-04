using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using LatencyBench.Core.Models;
using LatencyBench.Core.Network;
using LatencyBench.Core.Tweaks.DevicePower;
using LatencyBench.Core.Tweaks.Input;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Network;
using LatencyBench.Core.Tweaks.Power;
using LatencyBench.Core.Tweaks.Services;
using LatencyBench.Core.Tweaks.Storage;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// Virtual and non-sealed so a test can substitute a catalog of tweaks that record what happened
/// instead of writing to the registry. Every other consumer uses the real list.
/// </summary>
public class TweakCatalog
{
    private const string MmcssGamesTaskPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    public virtual IReadOnlyList<ITweak> BuildAll()
    {
        PowerCfgRunner powerCfg = new PowerCfgRunner();
        TweakBackupStore backupStore = new TweakBackupStore();
        UsbTreeEnumerator treeEnumerator = new UsbTreeEnumerator();
        NetworkAdapterEnumerator nicEnumerator = new NetworkAdapterEnumerator();
        return new ITweak[]
        {
            new HighPerformancePlanTweak(powerCfg, backupStore),
            new PowerCfgAcValueTweak(new TweakDefinition
            {
                Id = "power.usb-selective-suspend",
                Category = TweakCategory.Power,
                Name = "Disable USB selective suspend",
                Description = "Stops Windows from selectively suspending USB ports to save power under the active plan.",
                Risk = TweakRisk.Safe
            }, powerCfg, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0u, 1u, backupStore),
            new PowerCfgAcValueTweak(new TweakDefinition
            {
                Id = "cpu.min-processor-state",
                Category = TweakCategory.Cpu,
                Name = "Minimum processor state 100%",
                Description = "Keeps CPU cores out of idle/throttled states under the active plan, trading power draw for lower latency.",
                Risk = TweakRisk.Safe
            }, powerCfg, "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100u, 5u, backupStore),
            new PowerCfgAcValueTweak(new TweakDefinition
            {
                Id = "cpu.boost-mode-aggressive",
                Category = TweakCategory.Cpu,
                Name = "Aggressive CPU boost mode",
                Description = "Sets processor performance boost mode to Aggressive under the active plan.",
                Risk = TweakRisk.Safe
            }, powerCfg, "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", 2u, 1u, backupStore),
            new PowerCfgAcValueTweak(new TweakDefinition
            {
                Id = "cpu.disable-core-parking",
                Category = TweakCategory.Cpu,
                Name = "Disable core parking",
                Description = "Keeps all logical processors unparked under the active plan instead of letting Windows idle unused cores.",
                Risk = TweakRisk.Safe
            }, powerCfg, "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", 100u, 0u, backupStore),
            new DevicePowerManagementTweak(new TweakDefinition
            {
                Id = "usb.disable-power-management",
                Category = TweakCategory.Usb,
                Name = "Disable power management on USB hubs",
                Description = "Unchecks \"Allow the computer to turn off this device to save power\" for every USB host controller and hub.",
                Risk = TweakRisk.Safe
            }, () => FlattenUsbHubsAndControllers(treeEnumerator), backupStore),
            new PointerPrecisionTweak(backupStore),
            new KeyboardRepeatRateTweak(backupStore),
            new RegistryDwordTweak(new TweakDefinition
            {
                Id = "gpu.hardware-scheduling",
                Category = TweakCategory.Gpu,
                Name = "Hardware-accelerated GPU scheduling",
                Description = "Enables Windows' Hardware-accelerated GPU Scheduling. Requires a reboot to take effect.",
                Risk = TweakRisk.RequiresReboot,
                RequiresReboot = true
            }, RegistryHive.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers", "HwSchMode", 2, 1, backupStore),
            new TrimEnabledTweak(backupStore),
			// "Disable scheduled defrag" was removed rather than fixed. On Windows 8 and later that
			// task is the Storage Optimizer: on an SSD it does not defragment at all, it issues the
			// scheduled retrim. Disabling it therefore gives up periodic TRIM — which degrades write
			// latency over time — in exchange for no latency benefit whatsoever, since the task only
			// runs during maintenance windows when the machine is already idle.
			new NagleAlgorithmTweak(backupStore),
            new DevicePowerManagementTweak(new TweakDefinition
            {
                Id = "network.disable-power-management",
                Category = TweakCategory.Network,
                Name = "Disable power management on network adapters",
                Description = "Unchecks \"Allow the computer to turn off this device to save power\" for every network adapter.",
                Risk = TweakRisk.Safe
            }, () => (from a in nicEnumerator.EnumeratePhysicalAdapters()
                select a.InstanceId).ToList(), backupStore),
            new RegistryDwordTweak(new TweakDefinition
            {
                Id = "network.throttling-index",
                Category = TweakCategory.Network,
                Name = "Disable network throttling index",
                Description = "Removes the multimedia network-throttling cap Windows applies to background network traffic.",
                Risk = TweakRisk.Safe
            }, RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile", "NetworkThrottlingIndex", -1, 10, backupStore),
            new SysMainServiceTweak(backupStore),
			// --- Multimedia class scheduler -------------------------------------------------
			// MMCSS boosts threads that register with a named task. Games and audio engines
			// register with the "Games" and "Pro Audio" profiles, so these two values change how
			// the scheduler treats them.
			//
			// Only the two settings Microsoft documents are exposed. The profile also contains a
			// "GPU Priority" value that is copied around gaming guides constantly; its semantics
			// are not documented anywhere, nothing in the graphics stack is known to read it, and
			// a tweak whose effect cannot be stated is exactly what this app is meant to replace.
			new RegistryStringTweak(new TweakDefinition
            {
                Id = "mmcss.games-scheduling-category",
                Category = TweakCategory.Cpu,
                Name = "MMCSS scheduling category: High for games",
                Description = "Raises the multimedia scheduler's category for threads registered under the Games profile, so they are scheduled ahead of ordinary work.",
                Risk = TweakRisk.Safe
            }, RegistryHive.LocalMachine, MmcssGamesTaskPath, "Scheduling Category", "High", "Medium", backupStore),
            new RegistryStringTweak(new TweakDefinition
            {
                Id = "mmcss.games-sfio-priority",
                Category = TweakCategory.Cpu,
                Name = "MMCSS scheduled I/O priority: High for games",
                Description = "Raises the I/O priority the multimedia scheduler grants threads registered under the Games profile.",
                Risk = TweakRisk.Safe
            }, RegistryHive.LocalMachine, MmcssGamesTaskPath, "SFIO Priority", "High", "Normal", backupStore),
			// --- Timer resolution -----------------------------------------------------------
			new RegistryDwordTweak(new TweakDefinition
            {
                Id = "timer.global-resolution-requests",
                Category = TweakCategory.Cpu,
                Name = "Restore global timer resolution requests",
                Description = "Makes a timer resolution request from any process apply machine-wide again, as it did before Windows 10 version 2004. Requires a reboot.",
                Risk = TweakRisk.RequiresReboot,
                RequiresReboot = true
            }, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                "GlobalTimerResolutionRequests", 1, 0, backupStore),
            // --- Storage ----------------------------------------------------------------------
            new LastAccessTimestampTweak(backupStore),
            // --- Scheduler --------------------------------------------------------------------
            // Win32PrioritySeparation is the registry value behind System Properties > Advanced >
            // Performance Options > Advanced > "Adjust for best performance of: Programs" (client
            // default, value 2 — short, variable quanta with a foreground priority boost) versus
            // "Background services" (the value Windows Server ships with instead). This restores
            // the documented client default rather than asserting some new "better" value, which is
            // why applying it is safe: it is what a clean install of this edition already has.
            new RegistryDwordTweak(new TweakDefinition
            {
                Id = "system.win32-priority-separation",
                Category = TweakCategory.Cpu,
                Name = "Restore foreground-app scheduling priority",
                Description = "Restores Win32PrioritySeparation to 2 — the default Windows client scheduling behaviour that favours the foreground app — matching System Properties > Advanced > Performance Options > \"Adjust for best performance of: Programs\".",
                Risk = TweakRisk.Safe
            }, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 2, 2, backupStore)
        };
    }

    private static IReadOnlyList<string> FlattenUsbHubsAndControllers(UsbTreeEnumerator enumerator)
    {
        List<string> results = new List<string>();
        foreach (UsbDeviceNode item in enumerator.EnumerateHostControllers())
        {
            Visit(item);
        }
        return results;
        void Visit(UsbDeviceNode node)
        {
            if (node.IsHostController || node.IsHub)
            {
                results.Add(node.InstanceId);
            }
            foreach (UsbDeviceNode child in node.Children)
            {
                Visit(child);
            }
        }
    }
}
