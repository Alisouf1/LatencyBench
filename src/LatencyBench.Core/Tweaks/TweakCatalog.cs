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

public sealed class TweakCatalog
{
	public IReadOnlyList<ITweak> BuildAll()
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
			new TrimEnabledTweak(),
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
			new SysMainServiceTweak(backupStore)
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
