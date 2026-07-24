using LatencyBench.Core.Models;

namespace LatencyBench.Core.Advisor;

/// <summary>
/// Warns when a 2.4GHz wireless receiver (a mouse/keyboard dongle) is plugged into a USB 3.0+
/// (SuperSpeed) port. USB 3.0's signaling radiates broadband interference that overlaps the 2.4GHz
/// ISM band used by these receivers — a well-documented issue (Intel and USB-IF have both published
/// notes on it) that shows up as dropped inputs or added latency, not a driver or software problem.
/// Rule-based and pure, like the other advisors.
/// </summary>
public static class WirelessInterferenceAdvisor
{
    /// <summary>Matches a receiver/dongle by name — there's no dedicated device class for one, so this is the same name-based reasoning already used elsewhere in this app (e.g. UsbDeviceNodeExtensions.GetBestDisplayName). Deliberately narrow ("receiver"/"dongle") rather than including "wireless" alone, which would also match Wi-Fi adapters that aren't the same kind of concern here.</summary>
    private static readonly string[] ReceiverKeywords = ["receiver", "dongle"];

    /// <summary>Null when there's nothing to warn about, so callers test for a warning instead of string-matching this method's own prose.</summary>
    public static string? Generate(IReadOnlyList<UsbDeviceNode> hostControllers)
    {
        var warnings = new List<string>();

        foreach (var root in hostControllers)
        {
            foreach (var node in Flatten(root))
            {
                if (node.Speed is not (UsbSpeed.Super or UsbSpeed.SuperPlus) || !LooksLikeWirelessReceiver(node))
                {
                    continue;
                }

                warnings.Add(
                    $"{node.GetBestDisplayName()} (a 2.4GHz wireless receiver) is connected to a USB 3.0+ SuperSpeed port. " +
                    "USB 3.0 signaling radiates interference in the same 2.4GHz band these receivers use, which can cause dropped inputs or added latency.");
            }
        }

        return warnings.Count == 0 ? null : string.Join("\n\n", warnings.Distinct());
    }

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

    private static bool LooksLikeWirelessReceiver(UsbDeviceNode node)
        => ReceiverKeywords.Any(k => node.FriendlyName.Contains(k, StringComparison.OrdinalIgnoreCase));
}
