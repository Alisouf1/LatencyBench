using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Advisor;

namespace LatencyBench.Core.DpcIsr;

public sealed class DpcIsrTestResult
{
    public required double DurationSeconds { get; init; }

    public required double HighSpikesPerSecond { get; init; }

    public required double BorderlineSpikesPerSecond { get; init; }

    public required double HighestDpcMicroseconds { get; init; }

    public required double HighestIsrMicroseconds { get; init; }

    public required string TopDriverName { get; init; }

    public required double TopDriverMaxMicroseconds { get; init; }

    public IReadOnlyList<TunedDeviceConfig> TunedDevices { get; init; } = Array.Empty<TunedDeviceConfig>();

    /// <summary>
    /// Where this trace's interrupts landed, per core. Persisted with the run so the Affinity tab can
    /// average many traces together instead of trusting one short sample — a single trace is a noisy
    /// snapshot, and basing core assignment on the latest one alone made the recommendation jump after
    /// every trace. Traces saved before this field existed deserialize as empty and are simply skipped
    /// by the aggregator.
    /// </summary>
    public IReadOnlyList<CoreLoad> CoreLoads { get; init; } = Array.Empty<CoreLoad>();

    public DateTime SavedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// The grouping key the comparison uses to decide which traces were taken under the same settings.
    ///
    /// Devices are collapsed by category and setting ("USB controller×3: MSI on, IRQ High") rather than
    /// listed individually: the captured set spans every GPU, USB controller and audio device, and
    /// spelling out full chipset names would run to hundreds of characters in a message meant to be
    /// read. Collapsing keeps it legible while still changing whenever any device's MSI or IRQ priority
    /// changes — which is the whole point of the key. Ordering is fully determined so two traces taken
    /// under identical settings always produce a byte-identical label.
    /// </summary>
    public string ConfigLabel => (TunedDevices.Count == 0)
        ? "settings not captured"
        : string.Join(" | ", TunedDevices
            .GroupBy(d => new
            {
                // Traces saved before Category existed fall back to the device name, preserving their
                // original per-device label instead of collapsing them under a meaningless "".
                Group = string.IsNullOrEmpty(d.Category) ? d.Name : d.Category,
                d.MsiEnabled,
                d.Priority,
            })
            .OrderBy(g => g.Key.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.MsiEnabled)
            .ThenBy(g => g.Key.Priority)
            .Select(g => g.Count() > 1
                ? $"{g.Key.Group}×{g.Count()}: MSI {(g.Key.MsiEnabled ? "on" : "off")}, IRQ {g.Key.Priority}"
                : $"{g.Key.Group}: MSI {(g.Key.MsiEnabled ? "on" : "off")}, IRQ {g.Key.Priority}"));
}
