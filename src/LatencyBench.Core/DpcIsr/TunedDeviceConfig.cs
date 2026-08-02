using LatencyBench.Core.Models;

namespace LatencyBench.Core.DpcIsr;

/// <param name="Category">Which device class this is ("GPU", "USB controller", "Audio controller"). Defaults to empty so traces saved before the field existed still deserialize; <see cref="DpcIsrTestResult.ConfigLabel"/> falls back to <paramref name="Name"/> when it's missing.</param>
public sealed record TunedDeviceConfig(string Name, InterruptPriority Priority, bool MsiEnabled, string Category = "")
{
    public override string ToString()
    {
        return $"{Name}: MSI {(MsiEnabled ? "on" : "off")}, IRQ {Priority}";
    }
}
