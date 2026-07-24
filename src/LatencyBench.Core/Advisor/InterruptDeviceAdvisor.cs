using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;

namespace LatencyBench.Core.Advisor;

/// <summary>Rule-based recommendations for MSI mode and IRQ priority across all interrupt-capable devices — same style as the other advisors: no ML, just well-established heuristics.</summary>
public static class InterruptDeviceAdvisor
{
    /// <summary>Categories where forcing High IRQ priority is a well-established latency win. Left unopinionated for everything else (GPU/network/storage vendors have their own guidance).</summary>
    private static readonly HashSet<string> PriorityCriticalCategories = new(StringComparer.OrdinalIgnoreCase) { "USB controller", "Audio controller" };

    public static string Generate(IReadOnlyList<InterruptDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            return "No interrupt-capable devices were found to manage here.";
        }

        var lines = new List<string>();

        var msiDisabled = devices.Where(d => !d.IsMsiEnabled).ToList();
        if (msiDisabled.Count > 0)
        {
            lines.Add("MSI mode:");
            foreach (var device in msiDisabled)
            {
                lines.Add($"  • {device.FriendlyName} ({device.CategoryLabel}) is on legacy interrupts — enabling MSI usually reduces latency.");
            }
        }

        var priorityToRaise = devices
            .Where(d => PriorityCriticalCategories.Contains(d.CategoryLabel) && d.Priority != InterruptPriority.High)
            .ToList();
        if (priorityToRaise.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("IRQ priority:");
            foreach (var device in priorityToRaise)
            {
                lines.Add($"  • {device.FriendlyName} ({device.CategoryLabel}) is {device.Priority} priority — High is recommended for USB controllers and audio adapters.");
            }
        }

        if (lines.Count == 0)
        {
            return "Everything here is already configured for low, consistent interrupt latency — MSI is on where supported, and USB/audio devices are at High priority.";
        }

        return string.Join("\n", lines);
    }
}
