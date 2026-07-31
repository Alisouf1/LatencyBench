using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Detection runs against the real machine, so these assert invariants that must hold on any
/// Windows PC rather than specific hardware. A test that asserted "8 cores" would only pass on the
/// developer's box; a test that asserts "every logical processor appears exactly once" catches the
/// bugs that actually occur in topology parsing.
/// </summary>
public class SystemDetectionTests
{
	[Fact]
	public void TopologyReportsAtLeastOnePhysicalCore()
	{
		var topology = new CpuTopologyReader().Read();

		Assert.NotEmpty(topology.PhysicalCores);
	}

	[Fact]
	public void EveryLogicalProcessorBelongsToExactlyOnePhysicalCore()
	{
		var topology = new CpuTopologyReader().Read();

		var all = topology.PhysicalCores.SelectMany(core => core.LogicalProcessors).ToList();

		Assert.Equal(all.Count, all.Distinct().Count());
	}

	[Fact]
	public void LogicalProcessorCountMatchesTheRuntime()
	{
		var topology = new CpuTopologyReader().Read();

		// Environment.ProcessorCount is the count Windows reports to the process. On a single-group
		// machine — everything below 65 logical processors — the topology walk must agree exactly.
		if (!topology.UsesMultipleProcessorGroups)
		{
			Assert.Equal(Environment.ProcessorCount, topology.LogicalProcessorCount);
			Assert.Equal(
				Environment.ProcessorCount,
				topology.PhysicalCores.Sum(core => core.LogicalProcessors.Count));
		}
	}

	[Fact]
	public void LogicalProcessorIndexesAreContiguousFromZero()
	{
		var topology = new CpuTopologyReader().Read();

		var all = topology.PhysicalCores
			.SelectMany(core => core.LogicalProcessors)
			.OrderBy(index => index)
			.ToList();

		Assert.Equal(Enumerable.Range(0, all.Count), all);
	}

	[Fact]
	public void PhysicalCoreCountNeverExceedsLogicalProcessorCount()
	{
		var topology = new CpuTopologyReader().Read();

		Assert.True(topology.PhysicalCores.Count <= topology.LogicalProcessorCount);
	}

	[Fact]
	public void CoreClassificationIsConsistentWithHybridDetection()
	{
		var topology = new CpuTopologyReader().Read();

		if (topology.IsHybrid)
		{
			// A hybrid CPU must have at least one of each, and no core left unclassified.
			Assert.Contains(topology.PhysicalCores, core => core.Class == CoreClass.Performance);
			Assert.Contains(topology.PhysicalCores, core => core.Class == CoreClass.Efficiency);
			Assert.DoesNotContain(topology.PhysicalCores, core => core.Class == CoreClass.Uniform);
		}
		else
		{
			Assert.All(topology.PhysicalCores, core => Assert.Equal(CoreClass.Uniform, core.Class));
		}
	}

	[Fact]
	public void PerformanceCoresAreNeverEmpty()
	{
		var topology = new CpuTopologyReader().Read();

		Assert.NotEmpty(topology.PerformanceCores);
	}

	[Fact]
	public void SimultaneousMultithreadingFlagAgreesWithTheLogicalProcessorCount()
	{
		var topology = new CpuTopologyReader().Read();

		foreach (var core in topology.PhysicalCores.Where(c => c.LogicalProcessors.Count > 1))
		{
			Assert.True(core.IsSimultaneousMultithreaded);
		}
	}

	[Fact]
	public void NumaNodesCoverEveryLogicalProcessorWhenReported()
	{
		var topology = new CpuTopologyReader().Read();

		if (topology.NumaNodes.Count == 0)
		{
			return;
		}

		var covered = topology.NumaNodes.SelectMany(node => node.LogicalProcessors).Distinct().Count();
		Assert.Equal(topology.LogicalProcessorCount, covered);
	}

	[Fact]
	public void CoreOfResolvesEveryLogicalProcessor()
	{
		var topology = new CpuTopologyReader().Read();

		for (int i = 0; i < topology.LogicalProcessorCount; i++)
		{
			Assert.NotNull(topology.CoreOf(i));
		}
	}

	[Fact]
	public void CacheLevelsAreSaneWhenReported()
	{
		var topology = new CpuTopologyReader().Read();

		Assert.All(topology.Caches, cache =>
		{
			Assert.InRange(cache.Level, (byte)1, (byte)4);
			Assert.True(cache.SizeBytes > 0);
			Assert.True(cache.LineSizeBytes > 0);
		});
	}

	[Fact]
	public void CpuIdentityIsPopulated()
	{
		var cpu = new CpuInfoReader().Read();

		Assert.False(string.IsNullOrWhiteSpace(cpu.Name));
		Assert.True(cpu.LogicalProcessorCount > 0);
		Assert.True(cpu.PhysicalCoreCount > 0);
	}

	[Fact]
	public void WindowsVersionIsNotTheShimmedValue()
	{
		var windows = new WindowsInfoReader().Read();

		// GetVersionEx would report 6.2/9200 to an unmanifested process. Anything running these
		// tests is Windows 10 or later, so a build below 10240 means the shim was hit.
		Assert.Equal(10, windows.MajorVersion);
		Assert.True(windows.BuildNumber >= 10240, $"Build number {windows.BuildNumber} looks shimmed.");
	}

	[Fact]
	public void WindowsProductNameIsCorrectedForWindows11()
	{
		var windows = new WindowsInfoReader().Read();

		if (windows.IsWindows11)
		{
			// The raw registry value still says "Windows 10" on every Windows 11 install.
			Assert.DoesNotContain("Windows 10", windows.ProductName, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void WindowsBuildClassificationIsMutuallyExclusive()
	{
		var windows = new WindowsInfoReader().Read();

		Assert.False(windows.IsWindows10 && windows.IsWindows11);
	}

	[Fact]
	public void StorageEnumerationReportsSaneDevices()
	{
		var devices = new StorageEnumerator().Enumerate();

		// A machine running these tests booted from something.
		Assert.NotEmpty(devices);
		Assert.All(devices, device =>
		{
			Assert.False(string.IsNullOrWhiteSpace(device.FriendlyName));
			if (device.SizeBytes is { } size)
			{
				Assert.True(size > 0);
			}
		});
	}

	[Fact]
	public void NvmeDevicesAreAlwaysReportedAsSolidState()
	{
		var devices = new StorageEnumerator().Enumerate();

		Assert.All(
			devices.Where(device => device.BusType == StorageBusType.Nvme),
			device => Assert.True(device.IsSolidState));
	}

	[Fact]
	public void SomeDriveLetterMapsToAPhysicalDevice()
	{
		var devices = new StorageEnumerator().Enumerate();

		Assert.Contains(devices, device => device.DriveLetters.Count > 0);
	}

	[Fact]
	public async Task UsbAudioDevicesAreNotClassifiedAsPciAttached()
	{
		// Every USB device has a PCI ancestor eventually — its host controller — so a naive walk up
		// the tree reports a USB microphone as PCI-attached and erases the distinction entirely.
		SystemProfile profile = await new SystemProfiler().GetAsync();

		var usbAudio = profile.AudioDevices
			.Where(device => device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
			.ToList();

		Assert.All(usbAudio, device => Assert.False(
			device.IsPciController,
			$"{device.FriendlyName} is a USB device but was reported as PCI-attached."));
	}

	[Fact]
	public async Task NetworkAdaptersExcludeSoftwarePseudoAdapters()
	{
		SystemProfile profile = await new SystemProfiler().GetAsync();

		Assert.DoesNotContain(profile.NetworkAdapters, adapter =>
			adapter.FriendlyName.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
			|| adapter.FriendlyName.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase));

		Assert.All(profile.NetworkAdapters, adapter => Assert.False(
			adapter.InstanceId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase),
			$"{adapter.FriendlyName} is root-enumerated and therefore virtual."));
	}

	[Fact]
	public void PhysicalAdapterEnumerationIsASubsetOfAllAdapters()
	{
		var enumerator = new LatencyBench.Core.Network.NetworkAdapterEnumerator();

		var all = enumerator.EnumerateAdapters().Select(a => a.InstanceId).ToHashSet();
		var physical = enumerator.EnumeratePhysicalAdapters().Select(a => a.InstanceId).ToList();

		Assert.All(physical, id => Assert.Contains(id, all));
	}

	[Fact]
	public void StorageDevicesReportTheirSize()
	{
		// The first implementation used IOCTL_DISK_GET_LENGTH_INFO, which is declared FILE_READ_ACCESS
		// and therefore failed on the zero-access handle used here — every drive came back as 0 bytes.
		var devices = new StorageEnumerator().Enumerate();

		Assert.Contains(devices, device => device.SizeBytes is > 0);
	}

	[Fact]
	public void HardwareSchedulingSupportIsNotContradictory()
	{
		var windows = new WindowsInfoReader().Read();

		if (windows.HardwareAcceleratedGpuScheduling == FeatureState.Enabled)
		{
			Assert.True(
				windows.SupportsHardwareAcceleratedGpuScheduling,
				"HAGS cannot be enabled on a system that does not support it.");
		}
	}

	[Fact]
	public async Task FullProfileCapturesWithoutFatalError()
	{
		var profiler = new SystemProfiler();

		SystemProfile profile = await profiler.GetAsync();

		Assert.NotNull(profile.Cpu);
		Assert.NotNull(profile.Windows);
		Assert.True(profile.Memory.TotalPhysicalBytes > 0);
		Assert.NotEqual(default, profile.CapturedAt);
	}

	[Fact]
	public async Task ProfileIsCachedAfterTheFirstCapture()
	{
		var profiler = new SystemProfiler();

		SystemProfile first = await profiler.GetAsync();
		SystemProfile second = await profiler.GetAsync();

		Assert.Same(first, second);
	}

	[Fact]
	public async Task ConcurrentCallersShareASingleCapture()
	{
		var profiler = new SystemProfiler();

		SystemProfile[] results = await Task.WhenAll(
			Enumerable.Range(0, 8).Select(_ => profiler.GetAsync()));

		Assert.All(results, result => Assert.Same(results[0], result));
	}

	[Fact]
	public void CurrentIsNullBeforeAnyCaptureAndNeverBlocks()
	{
		var profiler = new SystemProfiler();

		Assert.Null(profiler.Current);
	}

	[Fact]
	public async Task ProfileCaptureStaysWithinAnInteractiveBudget()
	{
		// The whole point of doing detection off the UI thread is that it is not instant, but it
		// still has to be fast enough to run at startup without the window feeling stalled.
		var profiler = new SystemProfiler();

		var stopwatch = Stopwatch.StartNew();
		await profiler.RefreshAsync();
		stopwatch.Stop();

		Assert.True(
			stopwatch.ElapsedMilliseconds < 3000,
			$"Full hardware detection took {stopwatch.ElapsedMilliseconds} ms.");
	}

	[Fact]
	public async Task ProfileWarningsAreDescriptiveWhenPresent()
	{
		var profiler = new SystemProfiler();

		SystemProfile profile = await profiler.GetAsync();

		Assert.All(profile.Warnings, warning => Assert.False(string.IsNullOrWhiteSpace(warning)));
	}
}
