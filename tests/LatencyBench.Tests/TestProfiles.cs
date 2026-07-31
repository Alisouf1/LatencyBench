using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Builds machines that do not exist.
/// <para>
/// The recommendation rules have to be tested against a hybrid laptop, a virtual machine, a
/// 128-core workstation and a machine with memory integrity on — none of which is the machine
/// running the tests. Constructing the profile directly is what makes those cases reachable.
/// </para>
/// </summary>
public static class TestProfiles
{
	public static CpuTopology Topology(
		int physicalCores = 8,
		int threadsPerCore = 2,
		int efficiencyCores = 0,
		int numaNodes = 1,
		ushort processorGroups = 1)
	{
		var cores = new List<PhysicalCore>();
		int logical = 0;

		for (int core = 0; core < physicalCores; core++)
		{
			var logicalProcessors = new List<int>();
			for (int thread = 0; thread < threadsPerCore; thread++)
			{
				logicalProcessors.Add(logical++);
			}

			// Efficiency cores are placed last, matching how Windows reports them, and given the lower
			// efficiency class value.
			bool isEfficiency = core >= physicalCores - efficiencyCores;
			cores.Add(new PhysicalCore(
				core,
				logicalProcessors,
				Group: 0,
				EfficiencyClass: (byte)(isEfficiency ? 0 : 1),
				Class: efficiencyCores > 0
					? (isEfficiency ? CoreClass.Efficiency : CoreClass.Performance)
					: CoreClass.Uniform,
				IsSimultaneousMultithreaded: threadsPerCore > 1 && !isEfficiency));
		}

		int totalLogical = logical;

		// Split into contiguous blocks of whole cores, which is how real hardware is laid out — a
		// socket or a die owns a run of adjacent cores. Interleaving by parity instead would put every
		// core's first logical processor on the same node, so no core would ever be reachable on
		// node 1 and a NUMA-aware selection would look broken when it was not.
		int coresPerNode = Math.Max(1, (int)Math.Ceiling(cores.Count / (double)numaNodes));
		var nodes = Enumerable.Range(0, numaNodes)
			.Select(node => new NumaNode(
				node,
				cores
					.Skip(node * coresPerNode)
					.Take(coresPerNode)
					.SelectMany(core => core.LogicalProcessors)
					.ToList()))
			.Where(node => node.LogicalProcessors.Count > 0)
			.ToList();

		return new CpuTopology
		{
			PhysicalCores = cores,
			NumaNodes = nodes,
			Caches = Array.Empty<CacheLevel>(),
			LogicalProcessorCount = totalLogical,
			PackageCount = 1,
			ActiveProcessorGroupCount = processorGroups
		};
	}

	public static WindowsInfo Windows(
		int build = 26100,
		FeatureState memoryIntegrity = FeatureState.Disabled,
		FeatureState vbs = FeatureState.Disabled,
		FeatureState hags = FeatureState.Disabled,
		bool hagsSupported = true,
		bool modernStandby = false,
		bool virtualMachine = false) => new()
		{
			ProductName = "Windows 11 Pro",
			DisplayVersion = "24H2",
			MajorVersion = 10,
			MinorVersion = 0,
			BuildNumber = build,
			UpdateBuildRevision = 1000,
			EditionId = "Professional",
			HardwareAcceleratedGpuScheduling = hags,
			SupportsHardwareAcceleratedGpuScheduling = hagsSupported,
			VirtualizationBasedSecurity = vbs,
			MemoryIntegrity = memoryIntegrity,
			GameMode = FeatureState.Enabled,
			UsesModernStandby = modernStandby,
			IsVirtualMachine = virtualMachine
		};

	public static SystemProfile Profile(
		CpuTopology? topology = null,
		WindowsInfo? windows = null,
		bool hasBattery = false,
		int usbControllers = 3,
		int usbDevicesOnBusiest = 6,
		int networkAdapters = 1,
		IReadOnlyList<GpuInfo>? gpus = null,
		IReadOnlyList<UsbHostControllerSummary>? usbControllerOverride = null)
	{
		topology ??= Topology();

		var controllers = usbControllerOverride?.ToList() ?? Enumerable.Range(0, usbControllers)
			.Select(i => new UsbHostControllerSummary(
				$@"PCI\VEN_1022&DEV_15B6\{i}",
				$"USB xHCI Host Controller {i}",
				// The first controller is the busiest, so the rule under test has an unambiguous choice.
				i == 0 ? usbDevicesOnBusiest : 1))
			.ToList();

		var adapters = Enumerable.Range(0, networkAdapters)
			.Select(i => new NetworkAdapterSummary($@"PCI\VEN_10EC&DEV_8125\{i}", $"Gaming 2.5GbE Controller {i}"))
			.ToList();

		return new SystemProfile
		{
			Cpu = new CpuInfo
			{
				Name = "Test Processor",
				Vendor = CpuVendor.Amd,
				VendorIdentifier = "AuthenticAMD",
				NominalMhz = 4200,
				Topology = topology
			},
			Windows = windows ?? Windows(),
			Memory = new MemoryInfo
			{
				TotalPhysicalBytes = 32UL * 1024 * 1024 * 1024,
				AvailablePhysicalBytes = 20UL * 1024 * 1024 * 1024,
				LoadPercent = 37
			},
			Motherboard = new MotherboardInfo { Manufacturer = "Test", Product = "Board" },
			Gpus = gpus ?? new[]
			{
				new GpuInfo
				{
					Name = "Test GPU",
					InstanceId = @"PCI\VEN_10DE&DEV_2486\0",
					DriverVersion = "31.0.15.3699",
					Vendor = "NVIDIA",
					IsPrimary = true,
					DedicatedVideoMemoryBytes = 8UL * 1024 * 1024 * 1024
				}
			},
			StorageDevices = new[]
			{
				new StorageDeviceInfo
				{
					FriendlyName = "Test NVMe",
					InstanceId = @"\\.\PhysicalDrive0",
					BusType = StorageBusType.Nvme,
					IsSolidState = true,
					SizeBytes = 1_000_000_000_000,
					DriveLetters = new[] { "C:" }
				}
			},
			AudioDevices = Array.Empty<AudioDeviceInfo>(),
			UsbControllers = controllers,
			NetworkAdapters = adapters,
			HasBattery = hasBattery,
			CapturedAt = DateTimeOffset.UtcNow,
			Warnings = Array.Empty<string>()
		};
	}

	/// <summary>Every tweak reporting NotApplied, so rules that gate on state will fire.</summary>
	public static Dictionary<string, TweakState> AllNotApplied() => new()
	{
		["power.high-performance-plan"] = TweakState.NotApplied,
		["power.usb-selective-suspend"] = TweakState.NotApplied,
		["cpu.min-processor-state"] = TweakState.NotApplied,
		["cpu.boost-mode-aggressive"] = TweakState.NotApplied,
		["cpu.disable-core-parking"] = TweakState.NotApplied,
		["usb.disable-power-management"] = TweakState.NotApplied,
		["mouse.pointer-precision"] = TweakState.NotApplied,
		["keyboard.repeat-rate"] = TweakState.NotApplied,
		["gpu.hardware-scheduling"] = TweakState.NotApplied,
		["ssd.trim-enabled"] = TweakState.NotApplied,
		["network.disable-nagle"] = TweakState.NotApplied,
		["network.disable-power-management"] = TweakState.NotApplied,
		["network.throttling-index"] = TweakState.NotApplied,
		["aggressive.sysmain-disable"] = TweakState.NotApplied
	};

	public static Dictionary<string, TweakState> AllApplied() =>
		AllNotApplied().ToDictionary(pair => pair.Key, _ => TweakState.Applied);
}
