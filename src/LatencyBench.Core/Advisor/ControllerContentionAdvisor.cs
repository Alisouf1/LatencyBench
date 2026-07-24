using LatencyBench.Core.Models;

namespace LatencyBench.Core.Advisor;

/// <summary>
/// Warns when a latency-sensitive input device shares a USB host controller with devices that
/// reserve bandwidth or generate steady traffic. USB audio in particular uses isochronous transfers,
/// which reserve a fixed slice of every frame up front — a high-polling mouse on the same controller
/// is then competing for what's left. Rule-based and pure, like the other advisors.
/// </summary>
public static class ControllerContentionAdvisor
{
    /// <summary>Null when there's nothing to warn about, so callers test for a warning instead of string-matching this method's own prose (which silently breaks the moment the wording changes).</summary>
    public static string? Generate(IReadOnlyList<UsbDeviceNode> hostControllers, IReadOnlyList<int> controllerNumbers)
    {
        var warnings = new List<string>();

        for (var i = 0; i < hostControllers.Count; i++)
        {
            var subtree = Flatten(hostControllers[i]).ToList();
            var inputs = subtree.Where(IsLatencySensitive).Select(BestName).Distinct().ToList();
            if (inputs.Count == 0)
            {
                continue;
            }

            var heavy = subtree.Where(IsBandwidthHeavy).Select(BestName).Distinct().ToList();
            if (heavy.Count == 0)
            {
                continue;
            }

            var number = i < controllerNumbers.Count ? controllerNumbers[i] : i + 1;
            warnings.Add(
                $"USB Controller {number}: {string.Join(", ", inputs)} shares this controller with {string.Join(", ", heavy)}. " +
                "USB audio reserves bandwidth on every frame, so moving the input device to a controller of its own can steady its polling.");
        }

        return warnings.Count == 0 ? null : string.Join("\n\n", warnings);
    }

    /// <summary>Whether this controller (or anything beneath it) is a mouse or keyboard — used by the unified Dashboard advisor to know which controllers are worth a core-pinning recommendation.</summary>
    public static bool HasLatencySensitiveDevice(UsbDeviceNode controllerNode) => Flatten(controllerNode).Any(IsLatencySensitive);

    private static IEnumerable<UsbDeviceNode> Flatten(UsbDeviceNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsLatencySensitive(UsbDeviceNode node)
        => string.Equals(node.DeviceClass, "Mouse", StringComparison.OrdinalIgnoreCase)
        || string.Equals(node.DeviceClass, "Keyboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Audio (isochronous, reserves bandwidth up front), storage, and cameras — the classes that meaningfully compete for a controller's bandwidth.</summary>
    private static bool IsBandwidthHeavy(UsbDeviceNode node)
        => node.DeviceClass is { } c
        && (c.Equals("Media", StringComparison.OrdinalIgnoreCase)
            || c.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase)
            || c.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase)
            || c.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)
            || c.Equals("Image", StringComparison.OrdinalIgnoreCase)
            || c.Equals("Camera", StringComparison.OrdinalIgnoreCase));

    /// <summary>Prefer the device's own product name; fall back to the parent's when a sub-node is generic ("USB Input Device").</summary>
    private static string BestName(UsbDeviceNode node) => node.GetBestDisplayName();
}
