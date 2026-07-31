using System;
using System.Collections.Generic;

namespace LatencyBench.Core.SystemInfo.Models;

public enum CpuVendor
{
	Unknown,
	Intel,
	Amd
}

public sealed class CpuInfo
{
	public required string Name { get; init; }

	public required CpuVendor Vendor { get; init; }

	public required string VendorIdentifier { get; init; }

	/// <summary>Nominal clock as reported by firmware. Not the running clock, and not a boost clock.</summary>
	public required int NominalMhz { get; init; }

	public required CpuTopology Topology { get; init; }

	public int PhysicalCoreCount => Topology.PhysicalCores.Count;

	public int LogicalProcessorCount => Topology.LogicalProcessorCount;
}

public sealed class GpuInfo
{
	public required string Name { get; init; }

	public required string InstanceId { get; init; }

	/// <summary>The DirectX driver version from the driver's registry key, not the marketing version
	/// (for example 31.0.15.3699 rather than "537.13").</summary>
	public string? DriverVersion { get; init; }

	public DateTime? DriverDate { get; init; }

	public string? Vendor { get; init; }

	/// <summary>True for the display adapter Windows reports as attached to the primary desktop.</summary>
	public bool IsPrimary { get; init; }

	/// <summary>Dedicated video memory in bytes where the driver reports it.</summary>
	public ulong? DedicatedVideoMemoryBytes { get; init; }
}

public sealed class MotherboardInfo
{
	public string? Manufacturer { get; init; }

	public string? Product { get; init; }

	public string? SystemManufacturer { get; init; }

	public string? SystemProductName { get; init; }

	public string? BiosVendor { get; init; }

	public string? BiosVersion { get; init; }

	public string? BiosReleaseDate { get; init; }
}

public enum StorageBusType
{
	Unknown,
	Ata,
	Sata,
	Nvme,
	Usb,
	Raid,
	Sas,
	Scsi
}

public sealed class StorageDeviceInfo
{
	public required string FriendlyName { get; init; }

	public required string InstanceId { get; init; }

	public required StorageBusType BusType { get; init; }

	/// <summary>Null when Windows could not classify the media — common behind RAID controllers.</summary>
	public bool? IsSolidState { get; init; }

	public ulong? SizeBytes { get; init; }

	/// <summary>Drive letters backed by this physical device.</summary>
	public IReadOnlyList<string> DriveLetters { get; init; } = Array.Empty<string>();
}

public sealed class AudioDeviceInfo
{
	public required string FriendlyName { get; init; }

	public required string InstanceId { get; init; }

	/// <summary>
	/// The PCI device that actually owns this audio device's interrupt, or null for a USB interface
	/// whose interrupts belong to its host controller instead. For onboard audio this is the HD Audio
	/// controller, not the codec node that carries the friendly name.
	/// </summary>
	public string? PciControllerInstanceId { get; init; }

	/// <summary>True when this device's interrupts land on a PCI device whose affinity policy can be
	/// set. USB audio interfaces are tuned from the USB controller side instead.</summary>
	public bool IsPciController => PciControllerInstanceId is not null;
}

public sealed class MemoryInfo
{
	public required ulong TotalPhysicalBytes { get; init; }

	public required ulong AvailablePhysicalBytes { get; init; }

	public required uint LoadPercent { get; init; }
}

/// <summary>Everything the detection layer found, in one immutable snapshot.</summary>
public sealed class SystemProfile
{
	public required CpuInfo Cpu { get; init; }

	public required WindowsInfo Windows { get; init; }

	public required MemoryInfo Memory { get; init; }

	public required MotherboardInfo Motherboard { get; init; }

	public required IReadOnlyList<GpuInfo> Gpus { get; init; }

	public required IReadOnlyList<StorageDeviceInfo> StorageDevices { get; init; }

	public required IReadOnlyList<AudioDeviceInfo> AudioDevices { get; init; }

	public required IReadOnlyList<UsbHostControllerSummary> UsbControllers { get; init; }

	public required IReadOnlyList<NetworkAdapterSummary> NetworkAdapters { get; init; }

	/// <summary>
	/// True when the machine has a system battery. Several optimisations that are unambiguously good
	/// on a desktop — pinning the minimum processor state at 100%, keeping every core unparked — are
	/// a straight trade of battery life and heat on a laptop, so recommendations have to know.
	/// </summary>
	public required bool HasBattery { get; init; }

	/// <summary>When this snapshot was taken. Detection is cached, so consumers that care about
	/// freshness can check rather than assume.</summary>
	public required DateTimeOffset CapturedAt { get; init; }

	/// <summary>Non-fatal problems hit while probing — one probe failing must not lose the rest of
	/// the profile, so failures are collected here instead of thrown.</summary>
	public required IReadOnlyList<string> Warnings { get; init; }
}

/// <param name="NumaNode">
/// The node the controller is physically attached to, or null when Windows does not report one —
/// which is normal on a single-node consumer machine. On a multi-node system, servicing a device's
/// interrupts on a core in a different node means every access crosses the interconnect.
/// </param>
public sealed record UsbHostControllerSummary(
	string InstanceId,
	string FriendlyName,
	int AttachedDeviceCount,
	int? NumaNode = null);

public sealed record NetworkAdapterSummary(string InstanceId, string FriendlyName);
