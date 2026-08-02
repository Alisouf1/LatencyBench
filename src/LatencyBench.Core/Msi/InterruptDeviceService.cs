using Microsoft.Win32;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.Msi;

public sealed class InterruptDeviceService
{
    private const string MsiRelativePath = "Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";

    private const string PriorityRelativePath = "Device Parameters\\Interrupt Management\\Affinity Policy";

    public void SetMsiMode(string instanceId, bool enabled)
    {
        using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties", writable: true);
        registryKey.SetValue("MSISupported", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public void SetPriority(string instanceId, InterruptPriority priority)
    {
        using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy", writable: true);
        registryKey.SetValue("DevicePriority", (int)priority, RegistryValueKind.DWord);
    }
}
