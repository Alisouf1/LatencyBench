using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Not an assertion — a human-readable dump of what detection found on the machine running the
/// tests. Invariant tests catch parsing bugs that produce impossible values; they cannot catch a
/// plausible-but-wrong value, so this exists to be read.
/// </summary>
public class DetectionDump
{
    [Fact]
    public async Task WriteProfileToTempFile()
    {
        SystemProfile profile = await new SystemProfiler().GetAsync();
        var text = new StringBuilder();

        text.AppendLine("=== CPU ===");
        text.AppendLine($"Name              : {profile.Cpu.Name}");
        text.AppendLine($"Vendor            : {profile.Cpu.Vendor} ({profile.Cpu.VendorIdentifier})");
        text.AppendLine($"Nominal clock     : {profile.Cpu.NominalMhz} MHz");
        text.AppendLine($"Physical cores    : {profile.Cpu.PhysicalCoreCount}");
        text.AppendLine($"Logical processors: {profile.Cpu.LogicalProcessorCount}");
        text.AppendLine($"Hybrid            : {profile.Cpu.Topology.IsHybrid}");
        text.AppendLine($"SMT               : {profile.Cpu.Topology.HasSimultaneousMultithreading}");
        text.AppendLine($"Packages          : {profile.Cpu.Topology.PackageCount}");
        text.AppendLine($"Processor groups  : {profile.Cpu.Topology.ActiveProcessorGroupCount}");
        text.AppendLine($"NUMA nodes        : {profile.Cpu.Topology.NumaNodes.Count}");

        foreach (var core in profile.Cpu.Topology.PhysicalCores)
        {
            text.AppendLine(
                $"  core {core.CoreIndex,2}: lp=[{string.Join(",", core.LogicalProcessors)}] " +
                $"class={core.Class} eff={core.EfficiencyClass} smt={core.IsSimultaneousMultithreaded}");
        }

        foreach (var cache in profile.Cpu.Topology.Caches
            .GroupBy(c => (c.Level, c.Kind))
            .OrderBy(g => g.Key.Level))
        {
            text.AppendLine(
                $"  L{cache.Key.Level} {cache.Key.Kind,-11}: {cache.Count()} x {cache.First().SizeBytes / 1024} KB");
        }

        text.AppendLine();
        text.AppendLine("=== Windows ===");
        text.AppendLine($"Product           : {profile.Windows.ProductName}");
        text.AppendLine($"Display version   : {profile.Windows.DisplayVersion}");
        text.AppendLine($"Version           : {profile.Windows.VersionString}");
        text.AppendLine($"Edition           : {profile.Windows.EditionId}");
        text.AppendLine($"Windows 11        : {profile.Windows.IsWindows11}");
        text.AppendLine($"HAGS              : {profile.Windows.HardwareAcceleratedGpuScheduling} (supported: {profile.Windows.SupportsHardwareAcceleratedGpuScheduling})");
        text.AppendLine($"VBS               : {profile.Windows.VirtualizationBasedSecurity}");
        text.AppendLine($"Memory integrity  : {profile.Windows.MemoryIntegrity}");
        text.AppendLine($"Game Mode         : {profile.Windows.GameMode}");
        text.AppendLine($"Modern standby    : {profile.Windows.UsesModernStandby}");
        text.AppendLine($"Virtual machine   : {profile.Windows.IsVirtualMachine}");

        text.AppendLine();
        text.AppendLine("=== Memory ===");
        text.AppendLine($"Installed         : {profile.Memory.TotalPhysicalBytes / 1024.0 / 1024 / 1024:0.00} GB");
        text.AppendLine($"Available         : {profile.Memory.AvailablePhysicalBytes / 1024.0 / 1024 / 1024:0.00} GB");
        text.AppendLine($"Load              : {profile.Memory.LoadPercent}%");

        text.AppendLine();
        text.AppendLine("=== Motherboard ===");
        text.AppendLine($"Board             : {profile.Motherboard.Manufacturer} {profile.Motherboard.Product}");
        text.AppendLine($"System            : {profile.Motherboard.SystemManufacturer} {profile.Motherboard.SystemProductName}");
        text.AppendLine($"BIOS              : {profile.Motherboard.BiosVendor} {profile.Motherboard.BiosVersion} ({profile.Motherboard.BiosReleaseDate})");

        text.AppendLine();
        text.AppendLine("=== GPUs ===");
        foreach (var gpu in profile.Gpus)
        {
            text.AppendLine($"  {gpu.Name}");
            text.AppendLine($"    vendor={gpu.Vendor} primary={gpu.IsPrimary}");
            text.AppendLine($"    driver={gpu.DriverVersion} date={gpu.DriverDate:yyyy-MM-dd}");
            text.AppendLine($"    vram={(gpu.DedicatedVideoMemoryBytes ?? 0) / 1024.0 / 1024 / 1024:0.00} GB");
        }

        text.AppendLine();
        text.AppendLine("=== Storage ===");
        foreach (var device in profile.StorageDevices)
        {
            text.AppendLine(
                $"  {device.FriendlyName} bus={device.BusType} ssd={device.IsSolidState} " +
                $"size={(device.SizeBytes ?? 0) / 1024.0 / 1024 / 1024:0.0} GB letters=[{string.Join(",", device.DriveLetters)}]");
        }

        text.AppendLine();
        text.AppendLine("=== USB controllers ===");
        foreach (var controller in profile.UsbControllers)
        {
            text.AppendLine($"  {controller.FriendlyName} (attached: {controller.AttachedDeviceCount})");
        }

        text.AppendLine();
        text.AppendLine("=== Network ===");
        foreach (var adapter in profile.NetworkAdapters)
        {
            text.AppendLine($"  {adapter.FriendlyName}");
        }

        text.AppendLine();
        text.AppendLine("=== Audio ===");
        foreach (var audio in profile.AudioDevices)
        {
            text.AppendLine($"  {audio.FriendlyName} pci={audio.IsPciController}");
        }

        text.AppendLine();
        text.AppendLine("=== Warnings ===");
        text.AppendLine(profile.Warnings.Count == 0 ? "  (none)" : string.Join(Environment.NewLine, profile.Warnings.Select(w => "  " + w)));

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "latencybench-detection.txt"), text.ToString());
    }
}
