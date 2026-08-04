using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Interop;
using LatencyBench.Core.Models;
using LatencyBench.Core.Network;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Builds the machine's hardware and OS profile.
/// <para>
/// Two rules shape this class. First, one failing probe must never cost the whole profile: a
/// machine with an exotic storage controller should still get CPU, GPU and Windows detection, so
/// each probe is isolated and its failure is recorded as a warning. Second, nothing here is
/// allowed to be on the UI thread — the full profile takes tens of milliseconds, dominated by
/// device enumeration, so it is produced once, cached, and awaited.
/// </para>
/// </summary>
public sealed class SystemProfiler
{
    private readonly CpuInfoReader _cpuReader;
    private readonly WindowsInfoReader _windowsReader;
    private readonly MotherboardReader _motherboardReader;
    private readonly GpuEnumerator _gpuEnumerator;
    private readonly StorageEnumerator _storageEnumerator;
    private readonly UsbTreeEnumerator _usbTreeEnumerator;
    private readonly NetworkAdapterEnumerator _networkEnumerator;
    private readonly DeviceGuardReader _deviceGuardReader;
    private readonly MemoryModuleReader _memoryModuleReader;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private SystemProfile? _cached;

    public SystemProfiler(
        CpuInfoReader? cpuReader = null,
        WindowsInfoReader? windowsReader = null,
        MotherboardReader? motherboardReader = null,
        GpuEnumerator? gpuEnumerator = null,
        StorageEnumerator? storageEnumerator = null,
        UsbTreeEnumerator? usbTreeEnumerator = null,
        NetworkAdapterEnumerator? networkEnumerator = null,
        DeviceGuardReader? deviceGuardReader = null,
        MemoryModuleReader? memoryModuleReader = null)
    {
        _deviceGuardReader = deviceGuardReader ?? new DeviceGuardReader();
        _memoryModuleReader = memoryModuleReader ?? new MemoryModuleReader();
        _cpuReader = cpuReader ?? new CpuInfoReader();
        _windowsReader = windowsReader ?? new WindowsInfoReader();
        _motherboardReader = motherboardReader ?? new MotherboardReader();
        _gpuEnumerator = gpuEnumerator ?? new GpuEnumerator();
        _storageEnumerator = storageEnumerator ?? new StorageEnumerator();
        _usbTreeEnumerator = usbTreeEnumerator ?? new UsbTreeEnumerator();
        _networkEnumerator = networkEnumerator ?? new NetworkAdapterEnumerator();
    }

    /// <summary>The last profile produced, or null if none has been taken yet. Never blocks.</summary>
    public SystemProfile? Current => Volatile.Read(ref _cached);

    /// <summary>
    /// Returns the cached profile, taking one first if necessary. Concurrent callers during the
    /// initial detection all wait for the same run rather than each starting their own.
    /// </summary>
    public async Task<SystemProfile> GetAsync(CancellationToken cancellationToken = default)
    {
        SystemProfile? existing = Volatile.Read(ref _cached);
        if (existing is not null)
        {
            return existing;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-runs detection. Used after a change that alters the machine's shape — a device
    /// restart, or a reboot-requiring tweak being applied.</summary>
    public async Task<SystemProfile> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that queued behind an in-flight refresh gets that result rather than starting
            // a second full enumeration.
            SystemProfile? existing = Volatile.Read(ref _cached);
            if (existing is not null && existing.CapturedAt > DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(250))
            {
                return existing;
            }

            SystemProfile profile = await Task.Run(Capture, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, profile);
            return profile;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private SystemProfile Capture()
    {
        var warnings = new List<string>();

        CpuInfo cpu = Probe(warnings, "processor topology", _cpuReader.Read, FallbackCpu);
        WindowsInfo windows = Probe(warnings, "Windows version", _windowsReader.Read, FallbackWindows);
        windows = ApplyDeviceGuardRuntimeState(warnings, windows);
        MemoryInfo memory = Probe(warnings, "installed memory", ReadMemory, FallbackMemory);
        MotherboardInfo motherboard = Probe(warnings, "motherboard", _motherboardReader.Read, () => new MotherboardInfo());

        IReadOnlyList<GpuInfo> gpus = Probe<IReadOnlyList<GpuInfo>>(
            warnings, "display adapters", _gpuEnumerator.Enumerate, Array.Empty<GpuInfo>);

        IReadOnlyList<StorageDeviceInfo> storage = Probe<IReadOnlyList<StorageDeviceInfo>>(
            warnings, "storage devices", _storageEnumerator.Enumerate, Array.Empty<StorageDeviceInfo>);

        IReadOnlyList<AudioDeviceInfo> audio = Probe<IReadOnlyList<AudioDeviceInfo>>(
            warnings, "audio devices", ReadAudioDevices, Array.Empty<AudioDeviceInfo>);

        IReadOnlyList<UsbHostControllerSummary> usb = Probe<IReadOnlyList<UsbHostControllerSummary>>(
            warnings, "USB controllers", ReadUsbControllers, Array.Empty<UsbHostControllerSummary>);

        IReadOnlyList<NetworkAdapterSummary> network = Probe<IReadOnlyList<NetworkAdapterSummary>>(
            warnings, "network adapters", ReadNetworkAdapters, Array.Empty<NetworkAdapterSummary>);

        IReadOnlyList<MemoryModuleInfo> memoryModules = Probe<IReadOnlyList<MemoryModuleInfo>>(
            warnings, "memory modules", _memoryModuleReader.Read, Array.Empty<MemoryModuleInfo>);

        return new SystemProfile
        {
            Cpu = cpu,
            Windows = windows,
            Memory = memory,
            Motherboard = motherboard,
            Gpus = gpus,
            StorageDevices = storage,
            AudioDevices = audio,
            UsbControllers = usb,
            NetworkAdapters = network,
            MemoryModules = memoryModules,
            HasBattery = Probe(warnings, "battery presence", ReadHasBattery, () => false),
            CapturedAt = DateTimeOffset.UtcNow,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Overlays the running Device Guard state onto what the registry reported. The registry values
    /// are policy, not state, and are absent on a machine where Windows turned VBS on by default —
    /// so the WMI answer wins wherever it is definite.
    /// </summary>
    private WindowsInfo ApplyDeviceGuardRuntimeState(List<string> warnings, WindowsInfo windows)
    {
        var (vbs, memoryIntegrity) = Probe(
            warnings,
            "virtualisation-based security state",
            _deviceGuardReader.Read,
            () => (FeatureState.Unknown, FeatureState.Unknown));

        if (vbs == FeatureState.Unknown && memoryIntegrity == FeatureState.Unknown)
        {
            return windows;
        }

        return new WindowsInfo
        {
            ProductName = windows.ProductName,
            DisplayVersion = windows.DisplayVersion,
            MajorVersion = windows.MajorVersion,
            MinorVersion = windows.MinorVersion,
            BuildNumber = windows.BuildNumber,
            UpdateBuildRevision = windows.UpdateBuildRevision,
            EditionId = windows.EditionId,
            HardwareAcceleratedGpuScheduling = windows.HardwareAcceleratedGpuScheduling,
            SupportsHardwareAcceleratedGpuScheduling = windows.SupportsHardwareAcceleratedGpuScheduling,
            VirtualizationBasedSecurity = vbs != FeatureState.Unknown ? vbs : windows.VirtualizationBasedSecurity,
            MemoryIntegrity = memoryIntegrity != FeatureState.Unknown ? memoryIntegrity : windows.MemoryIntegrity,
            GameMode = windows.GameMode,
            UsesModernStandby = windows.UsesModernStandby,
            IsVirtualMachine = windows.IsVirtualMachine
        };
    }

    /// <summary>
    /// Runs one probe, converting any failure into a warning plus a documented fallback. Catching
    /// broadly is deliberate here: the caller is a detection pass whose entire job is to describe
    /// whatever it can, and there is no failure from a single hardware query that justifies
    /// returning no profile at all.
    /// </summary>
    private static T Probe<T>(List<string> warnings, string what, Func<T> probe, Func<T> fallback)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not detect {what}: {ex.Message}");
            return fallback();
        }
    }

    private static MemoryInfo ReadMemory()
    {
        var status = new SystemInfoApi.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<SystemInfoApi.MEMORYSTATUSEX>()
        };

        if (!SystemInfoApi.GlobalMemoryStatusEx(ref status))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        // ullTotalPhys is memory available to the OS, which is installed memory minus whatever the
        // firmware reserved. GetPhysicallyInstalledSystemMemory reports the SMBIOS figure the user
        // recognises, so it is preferred when the call succeeds.
        ulong total = status.ullTotalPhys;
        if (SystemInfoApi.GetPhysicallyInstalledSystemMemory(out ulong installedKilobytes) && installedKilobytes > 0)
        {
            total = installedKilobytes * 1024UL;
        }

        return new MemoryInfo
        {
            TotalPhysicalBytes = total,
            AvailablePhysicalBytes = status.ullAvailPhys,
            LoadPercent = status.dwMemoryLoad
        };
    }

    private static bool ReadHasBattery()
    {
        if (!SystemInfoApi.GetSystemPowerStatus(out SystemInfoApi.SYSTEM_POWER_STATUS status))
        {
            return false;
        }

        // An unknown flag is treated as "no battery" rather than "maybe": the consequence of a false
        // negative is a desktop-oriented recommendation on an unidentifiable machine, whereas a false
        // positive suppresses good advice on every desktop whose firmware reports oddly.
        return status.BatteryFlag != SystemInfoApi.BatteryFlagNoSystemBattery
            && status.BatteryFlag != SystemInfoApi.BatteryFlagUnknown;
    }

    private static IReadOnlyList<AudioDeviceInfo> ReadAudioDevices()
    {
        return DeviceClassEnumerator.Enumerate(DeviceClassEnumerator.MediaClass)
            .Select(device => new AudioDeviceInfo
            {
                FriendlyName = device.FriendlyName,
                InstanceId = device.InstanceId,
                // Resolved by walking up to the owning PCI device rather than by inspecting this
                // device's own ID: onboard audio presents as an HDAUDIO codec, and the interrupt
                // belongs to its parent HD Audio controller.
                PciControllerInstanceId = device.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                    ? device.InstanceId
                    : DeviceClassEnumerator.FindPciAncestor(device.DevInst)
            })
            .ToList();
    }

    private IReadOnlyList<UsbHostControllerSummary> ReadUsbControllers()
    {
        return _usbTreeEnumerator.EnumerateHostControllers()
            .Select(controller => new UsbHostControllerSummary(
                controller.InstanceId,
                controller.FriendlyName,
                CountDescendants(controller),
                ReadNumaNode(controller.InstanceId)))
            .ToList();
    }

    /// <summary>
    /// The NUMA node a device is attached to, or null when Windows does not report one. A single-node
    /// consumer machine reports nothing here, which is why null is a normal answer rather than a
    /// failure.
    /// </summary>
    private static int? ReadNumaNode(string instanceId)
    {
        if (CfgMgr32.CM_Locate_DevNodeW(out uint devInst, instanceId, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL)
            != CfgMgr32.CR_SUCCESS)
        {
            return null;
        }

        uint? node = CfgMgr32.GetUInt32Property(devInst, CfgMgr32.DEVPKEY_Device_Numa_Node);
        return node.HasValue ? (int)node.Value : null;
    }

    private static int CountDescendants(UsbDeviceNode node)
    {
        int count = 0;
        foreach (UsbDeviceNode child in node.Children)
        {
            count += 1 + CountDescendants(child);
        }

        return count;
    }

    private IReadOnlyList<NetworkAdapterSummary> ReadNetworkAdapters()
    {
        return _networkEnumerator.EnumeratePhysicalAdapters()
            .Select(adapter => new NetworkAdapterSummary(adapter.InstanceId, adapter.FriendlyName))
            .ToList();
    }

    private static CpuInfo FallbackCpu() => new()
    {
        Name = "Unknown processor",
        Vendor = CpuVendor.Unknown,
        VendorIdentifier = string.Empty,
        NominalMhz = 0,
        Topology = new CpuTopology
        {
            PhysicalCores = Array.Empty<PhysicalCore>(),
            NumaNodes = Array.Empty<NumaNode>(),
            Caches = Array.Empty<CacheLevel>(),
            LogicalProcessorCount = Environment.ProcessorCount,
            PackageCount = 1,
            ActiveProcessorGroupCount = 1
        }
    };

    private static WindowsInfo FallbackWindows() => new()
    {
        ProductName = "Windows",
        MajorVersion = 10,
        MinorVersion = 0,
        BuildNumber = 0,
        HardwareAcceleratedGpuScheduling = FeatureState.Unknown,
        SupportsHardwareAcceleratedGpuScheduling = false,
        VirtualizationBasedSecurity = FeatureState.Unknown,
        MemoryIntegrity = FeatureState.Unknown,
        GameMode = FeatureState.Unknown,
        UsesModernStandby = false,
        IsVirtualMachine = false
    };

    private static MemoryInfo FallbackMemory() => new()
    {
        TotalPhysicalBytes = 0,
        AvailablePhysicalBytes = 0,
        LoadPercent = 0
    };
}
