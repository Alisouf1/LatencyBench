using System;
using System.Linq;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Models;
using Microsoft.Win32;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises the affinity writer against a throwaway registry tree under HKCU rather than the real
/// device enumeration, so the validation and the exact values written can be asserted without
/// touching any actual device's interrupt policy.
/// </summary>
public sealed class InterruptAffinityServiceTests : IDisposable
{
	private const string FakeInstanceId = @"PCI\VEN_TEST&DEV_0001\3&11223344&0&E0";

	private readonly string _rootPath;
	private readonly RegistryKey _root;

	public InterruptAffinityServiceTests()
	{
		_rootPath = $@"Software\LatencyBenchTests\{Guid.NewGuid():N}";
		_root = Registry.CurrentUser.CreateSubKey(_rootPath, writable: true);
		_root.CreateSubKey(FakeInstanceId, writable: true).Dispose();
	}

	public void Dispose()
	{
		_root.Dispose();
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
		}
		catch (Exception)
		{
			// Leftover test keys under HKCU are harmless.
		}
	}

	private InterruptAffinityService CreateService() => new(treeEnumerator: null, enumRoot: _root);

	private RegistryKey? OpenPolicyKey() =>
		_root.OpenSubKey($@"{FakeInstanceId}\Device Parameters\Interrupt Management\Affinity Policy");

	[Fact]
	public void WritesPolicyPriorityAndMaskForASingleCore()
	{
		using var service = CreateService();

		service.SetSpecifiedCores(FakeInstanceId, new[] { 0 }, InterruptPriority.High);

		using RegistryKey? policy = OpenPolicyKey();
		Assert.NotNull(policy);
		Assert.Equal((int)InterruptAffinityPolicy.SpecifiedProcessors, policy!.GetValue("DevicePolicy"));
		Assert.Equal((int)InterruptPriority.High, policy.GetValue("DevicePriority"));
		Assert.Equal(1UL, AffinityMask.FromBytes((byte[])policy.GetValue("AssignmentSetOverride")!));
	}

	[Fact]
	public void WritesACombinedMaskForSeveralCores()
	{
		using var service = CreateService();
		int[] cores = Enumerable.Range(0, Math.Min(3, Environment.ProcessorCount)).ToArray();

		service.SetSpecifiedCores(FakeInstanceId, cores);

		using RegistryKey? policy = OpenPolicyKey();
		ulong mask = AffinityMask.FromBytes((byte[])policy!.GetValue("AssignmentSetOverride")!);
		Assert.Equal(cores, AffinityMask.ToCoreIndices(mask));
	}

	[Fact]
	public void RejectsAnEmptyCoreSelection()
	{
		// This is the dangerous case: DevicePolicy=SpecifiedProcessors with an all-zero mask tells
		// Windows to target the specified processors while naming none of them.
		using var service = CreateService();

		Assert.Throws<ArgumentException>(() => service.SetSpecifiedCores(FakeInstanceId, Array.Empty<int>()));
	}

	[Fact]
	public void RejectsAnEmptySelectionBeforeWritingAnything()
	{
		using var service = CreateService();

		Assert.Throws<ArgumentException>(() => service.SetSpecifiedCores(FakeInstanceId, Array.Empty<int>()));

		Assert.Null(OpenPolicyKey());
	}

	[Fact]
	public void RejectsACoreThatDoesNotExistOnThisMachine()
	{
		using var service = CreateService();

		Assert.Throws<ArgumentOutOfRangeException>(
			() => service.SetSpecifiedCores(FakeInstanceId, new[] { Environment.ProcessorCount }));
	}

	[Fact]
	public void RejectsANegativeCoreIndex()
	{
		using var service = CreateService();

		Assert.Throws<ArgumentOutOfRangeException>(() => service.SetSpecifiedCores(FakeInstanceId, new[] { -1 }));
	}

	[Fact]
	public void RejectsAnUndefinedInterruptPriority()
	{
		using var service = CreateService();

		Assert.Throws<ArgumentOutOfRangeException>(
			() => service.SetSpecifiedCores(FakeInstanceId, new[] { 0 }, (InterruptPriority)999));
	}

	[Fact]
	public void ThrowsForAnUnknownDeviceInstance()
	{
		using var service = CreateService();

		Assert.Throws<InvalidOperationException>(
			() => service.SetSpecifiedCores(@"PCI\VEN_NOPE&DEV_0000\0", new[] { 0 }));
	}

	[Fact]
	public void ClearOverrideRemovesThePolicy()
	{
		using var service = CreateService();
		service.SetSpecifiedCores(FakeInstanceId, new[] { 0 });

		service.ClearOverride(FakeInstanceId);

		Assert.Null(OpenPolicyKey());
	}

	[Fact]
	public void ClearOverrideLeavesTheSiblingMsiKeyIntact()
	{
		// The MSI settings live next door under Interrupt Management and are owned by a different
		// tab. Deleting the whole subtree here would silently wipe a controller's MSI mode.
		using var service = CreateService();
		service.SetSpecifiedCores(FakeInstanceId, new[] { 0 });

		using (RegistryKey msi = _root.CreateSubKey(
			$@"{FakeInstanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
			writable: true))
		{
			msi.SetValue("MSISupported", 1, RegistryValueKind.DWord);
		}

		service.ClearOverride(FakeInstanceId);

		using RegistryKey? msiAfter = _root.OpenSubKey(
			$@"{FakeInstanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
		Assert.NotNull(msiAfter);
		Assert.Equal(1, msiAfter!.GetValue("MSISupported"));
	}

	[Fact]
	public void ClearOverrideOnADeviceWithNoPolicyIsANoOp()
	{
		using var service = CreateService();

		service.ClearOverride(FakeInstanceId);

		Assert.Null(OpenPolicyKey());
	}

	[Fact]
	public void ReadPolicyReturnsWhatWasWritten()
	{
		using var service = CreateService();
		service.SetSpecifiedCores(FakeInstanceId, new[] { 0 }, InterruptPriority.High);

		HostControllerInfo info = service.ReadPolicy(FakeInstanceId, "Test controller");

		Assert.Equal(InterruptAffinityPolicy.SpecifiedProcessors, info.Policy);
		Assert.Equal(InterruptPriority.High, info.Priority);
		Assert.Equal(1UL, info.AffinityMask);
		Assert.Equal(Environment.ProcessorCount, info.LogicalProcessorCount);
	}

	[Fact]
	public void ApplyingTwiceOverwritesRatherThanAccumulating()
	{
		using var service = CreateService();
		int secondCore = Math.Min(1, Environment.ProcessorCount - 1);

		service.SetSpecifiedCores(FakeInstanceId, new[] { 0 });
		service.SetSpecifiedCores(FakeInstanceId, new[] { secondCore });

		using RegistryKey? policy = OpenPolicyKey();
		ulong mask = AffinityMask.FromBytes((byte[])policy!.GetValue("AssignmentSetOverride")!);
		Assert.Equal(new[] { secondCore }, AffinityMask.ToCoreIndices(mask));
	}
}
