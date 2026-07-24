using System.Text.RegularExpressions;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.Core.Advisor;

public enum FindingSeverity
{
    Suggestion,
    Warning,
    Critical,
}

public sealed record DiagnosisFinding(
    string Title,
    string Explanation,
    IReadOnlyList<string> Evidence,
    string RecommendedAction,
    FindingSeverity Severity);

/// <param name="Label">Real attached-device names for this controller, or a "USB Controller N" fallback — never the raw chipset name.</param>
/// <param name="ControllerNumber">Position in the current, live controller enumeration — used only as a fallback when a saved port result predates InstanceId tracking.</param>
/// <param name="InstanceId">The controller's durable PnP device instance ID — the reliable join key against PortRankResult.HostControllerInstanceId, since it can't drift the way a positional number can if a later enumeration ever orders or counts controllers differently.</param>
/// <param name="ContentionDetail">ControllerContentionAdvisor's message for this one controller, or null if it doesn't share bandwidth with anything.</param>
/// <param name="PinnedCoreInterruptSharePercent">The busiest pinned core's share of all system interrupts, from the latest DPC/ISR trace's core breakdown. Null when the controller uses the default policy or no trace has been run.</param>
/// <param name="HasLatencySensitiveDevice">Whether this controller carries a mouse or keyboard — see ControllerContentionAdvisor.HasLatencySensitiveDevice. Drives the core-pinning suggestion, which is only worth making for controllers actually serving latency-sensitive input.</param>
public sealed record ControllerSignal(
    string Label,
    int ControllerNumber,
    string InstanceId,
    string? ContentionDetail,
    InterruptAffinityPolicy Policy,
    IReadOnlyList<int> PinnedCores,
    double? PinnedCoreInterruptSharePercent,
    bool HasLatencySensitiveDevice);

/// <summary>
/// One MSI-mode entry — any interrupt-capable device category (GPU, network, audio, storage, or the
/// USB controller itself), not just GPUs. A mouse or keyboard doesn't have its own MSI/priority
/// settings; it's the USB controller carrying it that does, so this is what lets the Action Plan
/// recommend tuning *that* controller when it's the one producing interrupt spikes.
/// </summary>
/// <param name="ServiceName">The kernel driver name (e.g. "nvlddmkm"), used to match this device back to a DPC/ISR trace's top driver — without it there's no reliable way to tell which of several same-category devices a trace is actually about.</param>
public sealed record InterruptDeviceSignal(string Label, string CategoryLabel, string? ServiceName, bool MsiEnabled, InterruptPriority IrqPriority);

/// <summary>
/// Synthesizes signals from every tab — port test history, DPC/ISR traces, core affinity, and MSI/IRQ
/// state — into one ranked list of findings, each naming its cause and the evidence behind it. A
/// single-tab advisor can only say "this port is slow" or "this core is busy"; this connects them, so
/// a slow port whose controller shares a busy interrupt core is reported as one story instead of two
/// unrelated observations the user has to connect themselves.
///
/// Deliberately conservative: every rule reuses the same thresholds the individual tab advisors
/// already use (see ControllerContentionAdvisor, CoreAffinityAdvisor, DpcIsrComparisonGenerator), so
/// this never contradicts what a tab says on its own — it only adds the connections between them.
/// </summary>
public static class UnifiedDiagnosisGenerator
{
    private const int MaxFindings = 4;

    /// <summary>A worse port has to clear this bar over the best one before it's worth a headline finding — matches the "notably worse" bar AdvisorMessageGenerator already uses for single-port advice, so this doesn't manufacture urgency the port tab itself wouldn't.</summary>
    private const double NotablyWorseMultiplier = 2.0;

    /// <summary>The bar drops to this once the worst port's controller has a known contention cause — a smaller gap is still worth connecting to a plausible mechanism instead of being silently dropped and re-surfacing later as a vague, data-blind "controller shares bandwidth" note that ignores the number sitting right next to it.</summary>
    private const double NotablyWorseMultiplierWithKnownCause = 1.25;

    /// <summary>Matches DpcIsrViewModel's own "worth mentioning" bar for spike rate.</summary>
    private const double NoteworthySpikesPerSecond = 0.2;

    /// <summary>Matches CoreAffinityAdvisor's InterruptHotSharePercent — the point past which a core is a poor place to route more interrupts.</summary>
    private const double HotCoreSharePercent = 25.0;

    public static IReadOnlyList<DiagnosisFinding> Generate(
        IReadOnlyList<PortRankResult> portResults,
        IReadOnlyList<DpcIsrTestResult> dpcIsrTraces,
        IReadOnlyList<ControllerSignal> controllers,
        IReadOnlyList<CoreLoad> coreLoads,
        IReadOnlyList<InterruptDeviceSignal> interruptDevices,
        string? wirelessInterferenceWarning = null)
    {
        var portGroups = PortHistoryGrouping.GroupByPort(portResults);
        var latestTrace = dpcIsrTraces.OrderByDescending(t => t.SavedAt).FirstOrDefault();

        var findings = new List<DiagnosisFinding>();

        AddIfNotNull(findings, NoDataYet(portGroups, dpcIsrTraces));
        findings.AddRange(WorstPortFindings(portGroups, controllers));
        AddIfNotNull(findings, InterruptHotCoreConflict(latestTrace, controllers));
        AddIfNotNull(findings, LatencySensitiveControllerNotPinned(controllers, coreLoads));
        AddIfNotNull(findings, InterruptDeviceNotOptimized(latestTrace, interruptDevices));
        AddIfNotNull(findings, WirelessInterference(wirelessInterferenceWarning));
        AddIfNotNull(findings, UnresolvedContention(portGroups, controllers, findings));

        if (findings.Count == 0)
        {
            findings.Add(NothingSignificant(portGroups, latestTrace, controllers));
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .Take(MaxFindings)
            .ToList();
    }

    private static void AddIfNotNull(List<DiagnosisFinding> list, DiagnosisFinding? finding)
    {
        if (finding is not null)
        {
            list.Add(finding);
        }
    }

    private static DiagnosisFinding? NoDataYet(IReadOnlyList<PortHistoryGroup> portGroups, IReadOnlyList<DpcIsrTestResult> traces)
    {
        if (portGroups.Count > 0 || traces.Count > 0)
        {
            return null;
        }

        return new DiagnosisFinding(
            "No tests run yet",
            "There's nothing to diagnose until you've measured something.",
            [],
            "Start with a port test on your mouse or keyboard (Port test tab), then a DPC/ISR trace while gaming (DPC/ISR tab) so this plan has real data to work from.",
            FindingSeverity.Suggestion);
    }

    /// <summary>
    /// The no-op case, made to visibly cite what was actually checked. A bare "nothing found" reads
    /// exactly like the check silently ignored the data — this instead names the numbers it looked
    /// at and why they didn't cross a threshold, so it's clear every tab really was read.
    /// </summary>
    private static DiagnosisFinding NothingSignificant(
        IReadOnlyList<PortHistoryGroup> portGroups, DpcIsrTestResult? latestTrace, IReadOnlyList<ControllerSignal> controllers)
    {
        var evidence = new List<string>();

        // Same-cadence subset only (see PortHistoryGrouping.IsHighFrequency) — a keyboard's jitter
        // sits on a different scale than a mouse's, so it can't be cited as "how close" two ports are.
        var comparableGroups = portGroups.Where(g => g.IsHighFrequency).ToList();
        if (comparableGroups.Count == 1)
        {
            var only = comparableGroups[0].Best;
            evidence.Add($"Port test: only {only.PortLabel} tested so far ({only.AverageJitterMs:0.000} ms) — need a second port to compare against.");
        }
        else if (comparableGroups.Count > 1)
        {
            var best = comparableGroups[0].Best;
            var worst = comparableGroups[^1].Best;
            var percentWorse = best.AverageJitterMs is > 0
                ? (worst.AverageJitterMs - best.AverageJitterMs) / best.AverageJitterMs * 100.0
                : 0;
            evidence.Add($"Port test: {comparableGroups.Count} ports tested, {best.PortLabel} best ({best.AverageJitterMs:0.000} ms) to {worst.PortLabel} worst ({worst.AverageJitterMs:0.000} ms) — only {percentWorse:0}% apart, not the 2x gap that would flag one as a problem.");
        }

        if (latestTrace is not null)
        {
            evidence.Add($"DPC/ISR: last trace {latestTrace.HighSpikesPerSecond:0.00} spikes/s over 500 µs — {(latestTrace.HighSpikesPerSecond < NoteworthySpikesPerSecond ? "low enough to ignore" : "elevated, but no tuned device on this trace's top driver could be matched")}.");
        }

        if (controllers.Count > 0)
        {
            var pinned = controllers.Where(c => c.Policy == InterruptAffinityPolicy.SpecifiedProcessors).ToList();
            evidence.Add(pinned.Count == 0
                ? $"Affinity: none of your {controllers.Count} controller(s) are pinned to a specific core yet — nothing to check for a hot-core conflict."
                : $"Affinity: {pinned.Count} of {controllers.Count} controller(s) pinned, none on an overloaded core.");
        }

        return new DiagnosisFinding(
            "Nothing significant found",
            "Every tab's data above was checked against the same thresholds the individual tabs use — none of it crosses the bar for a problem.",
            evidence,
            "No action needed right now — re-run this check after you add more test results or change a setting.",
            FindingSeverity.Suggestion);
    }

    /// <summary>
    /// Runs the worst-vs-best comparison separately for each report cadence found in history (mouse-
    /// like continuous polling vs keyboard-like sporadic keystrokes — see
    /// PortHistoryGrouping.IsHighFrequency), instead of once across everything. Comparing a keyboard's
    /// structurally larger jitter against a mouse's would flag an ordinary keyboard as "the worst
    /// port" every single time, regardless of whether either port has any real problem.
    /// </summary>
    private static IEnumerable<DiagnosisFinding> WorstPortFindings(IReadOnlyList<PortHistoryGroup> portGroups, IReadOnlyList<ControllerSignal> controllers)
    {
        foreach (var sameCadenceGroups in portGroups.GroupBy(g => g.IsHighFrequency))
        {
            // GroupByPort already sorts ascending by best jitter; Where/GroupBy preserve that order.
            var sorted = sameCadenceGroups.ToList();
            var finding = WorstPortWithContention(sorted, controllers);
            if (finding is not null)
            {
                yield return finding;
            }
        }
    }

    private static DiagnosisFinding? WorstPortWithContention(IReadOnlyList<PortHistoryGroup> portGroups, IReadOnlyList<ControllerSignal> controllers)
    {
        if (portGroups.Count < 2)
        {
            return null;
        }

        var best = portGroups[0].Best;
        var worst = portGroups[^1].Best;
        if (best.AverageJitterMs is not double bestJitter || worst.AverageJitterMs is not double worstJitter || bestJitter <= 0)
        {
            return null;
        }

        var controller = FindController(worst, controllers);
        var hasKnownCause = controller?.ContentionDetail is not null;

        // A gap that wouldn't be worth mentioning on its own is worth connecting once there's already
        // a known, plausible mechanism for it — the point isn't a stricter statistical bar, it's not
        // silently discarding a real measurement that lines up with a cause we can already see.
        var bar = hasKnownCause ? NotablyWorseMultiplierWithKnownCause : NotablyWorseMultiplier;
        if (worstJitter < bestJitter * bar)
        {
            return null;
        }

        var percentWorse = (worstJitter - bestJitter) / bestJitter * 100.0;
        var evidence = new List<string>
        {
            $"Port test: {worst.PortLabel} averages {worstJitter:0.000} ms jitter vs {bestJitter:0.000} ms on {best.PortLabel} ({percentWorse:0}% worse).",
        };

        var action = $"Move the device on {worst.PortLabel} to {best.PortLabel} if the cabling allows it.";
        var explanation = "No specific cause identified beyond the port itself — worth testing this device on a different physical port.";

        if (hasKnownCause)
        {
            evidence.Add($"Affinity: {controller!.ContentionDetail}");
            explanation = "This port's controller shares bandwidth with another device — a well-established cause of added jitter, and the most likely explanation here.";
            action = $"Move the device on {worst.PortLabel} to a controller of its own, or to {best.PortLabel} — the shared bandwidth-heavy device on this controller is the likely cause.";
        }

        return new DiagnosisFinding(
            $"{worst.PortLabel} is worse than your best port",
            explanation,
            evidence,
            action,
            FindingSeverity.Warning);
    }

    private static DiagnosisFinding? InterruptHotCoreConflict(DpcIsrTestResult? latestTrace, IReadOnlyList<ControllerSignal> controllers)
    {
        if (latestTrace is null || latestTrace.HighSpikesPerSecond < NoteworthySpikesPerSecond)
        {
            return null;
        }

        var hotController = controllers
            .Where(c => c.Policy == InterruptAffinityPolicy.SpecifiedProcessors && c.PinnedCoreInterruptSharePercent is not null)
            .OrderByDescending(c => c.PinnedCoreInterruptSharePercent)
            .FirstOrDefault();

        if (hotController is null || hotController.PinnedCoreInterruptSharePercent < HotCoreSharePercent)
        {
            return null;
        }

        return new DiagnosisFinding(
            $"Controller {hotController.ControllerNumber} ({hotController.Label}) is pinned to your busiest interrupt core",
            $"Your last DPC/ISR trace measured {latestTrace.HighSpikesPerSecond:0.00} interrupt spikes/second over 500 µs, and this controller's pinned core is handling {hotController.PinnedCoreInterruptSharePercent:0}% of all system interrupts. A spike-heavy system combined with a controller stuck on the busiest core compounds the problem.",
            [
                $"DPC/ISR: {latestTrace.HighSpikesPerSecond:0.00} spikes/s over 500 µs (traced {latestTrace.DurationSeconds:0}s).",
                $"Affinity: Controller {hotController.ControllerNumber} pinned to a core handling {hotController.PinnedCoreInterruptSharePercent:0}% of interrupts.",
            ],
            $"In the Affinity tab, move Controller {hotController.ControllerNumber} off its current core to one of the quieter cores shown in the DPC/ISR core chart.",
            FindingSeverity.Critical);
    }

    /// <summary>
    /// Matches the latest trace's top driver against ANY MSI-mode device — GPU, network, audio,
    /// storage, or a USB controller itself (which is what actually carries a mouse/keyboard/USB
    /// microphone; those peripherals don't have their own MSI/priority settings). Generalizes what
    /// used to be a GPU-only rule, since interrupt spikes are just as often the USB controller or
    /// audio driver as the GPU.
    /// </summary>
    private static DiagnosisFinding? InterruptDeviceNotOptimized(DpcIsrTestResult? latestTrace, IReadOnlyList<InterruptDeviceSignal> interruptDevices)
    {
        if (latestTrace is null || latestTrace.HighSpikesPerSecond < NoteworthySpikesPerSecond)
        {
            return null;
        }

        var driverService = System.IO.Path.GetFileNameWithoutExtension(latestTrace.TopDriverName);
        var device = interruptDevices.FirstOrDefault(d => d.ServiceName is not null && string.Equals(d.ServiceName, driverService, StringComparison.OrdinalIgnoreCase));

        // No confident match — same lesson as the earlier "unknown driver" GPU mismatch bug: don't
        // guess which device a trace is about when the driver name can't be tied to one.
        if (device is null || (device.MsiEnabled && device.IrqPriority == InterruptPriority.High))
        {
            return null;
        }

        var issues = new List<string>();
        if (!device.MsiEnabled)
        {
            issues.Add("MSI mode is off");
        }

        if (device.IrqPriority != InterruptPriority.High)
        {
            issues.Add($"IRQ priority is {device.IrqPriority}");
        }

        return new DiagnosisFinding(
            $"{device.Label} is producing interrupt spikes and isn't tuned",
            $"Your last DPC/ISR trace's top source was {latestTrace.TopDriverName} at {latestTrace.HighSpikesPerSecond:0.00} spikes/s, and {device.Label} ({device.CategoryLabel}) currently has {string.Join(" and ", issues)}.",
            [
                $"DPC/ISR: top driver {latestTrace.TopDriverName}, {latestTrace.HighSpikesPerSecond:0.00} spikes/s.",
                $"MSI mode: {device.Label} — MSI {(device.MsiEnabled ? "on" : "off")}, IRQ priority {device.IrqPriority}.",
            ],
            $"In the MSI mode tab, enable MSI and set IRQ priority to High for {device.Label}, restart the device, then re-trace to confirm the spike rate drops.",
            FindingSeverity.Warning);
    }

    /// <summary>A hardware-topology fact, not test-derived — see WirelessInterferenceAdvisor for the mechanism (USB 3.0 SuperSpeed signaling shares the 2.4GHz band a wireless mouse/keyboard receiver uses).</summary>
    private static DiagnosisFinding? WirelessInterference(string? warning)
    {
        if (warning is null)
        {
            return null;
        }

        return new DiagnosisFinding(
            "Wireless receiver near USB 3.0 interference",
            warning,
            [$"USB tree: {warning}"],
            "Move the wireless receiver to a USB 2.0 port, or use a short USB extension cable to get it away from USB 3.0 ports and cables.",
            FindingSeverity.Warning);
    }

    /// <summary>
    /// Proactive, not reactive: recommends a core for a mouse/keyboard-carrying controller that's
    /// still on Windows' default assignment, the same recommendation CoreAffinityAdvisor already gives
    /// on the Affinity tab itself (GenerateFromInterruptLoad's "nothing pinned" branch) — surfaced here
    /// too, since the whole point of the Action Plan is not needing to visit every tab to get it.
    /// </summary>
    private static DiagnosisFinding? LatencySensitiveControllerNotPinned(IReadOnlyList<ControllerSignal> controllers, IReadOnlyList<CoreLoad> coreLoads)
    {
        if (coreLoads.Count == 0)
        {
            return null;
        }

        var unpinned = controllers
            .Where(c => c.HasLatencySensitiveDevice && c.Policy == InterruptAffinityPolicy.MachineDefault)
            .ToList();

        if (unpinned.Count == 0)
        {
            return null;
        }

        var quietest = coreLoads.OrderBy(c => c.SharePercent).First();
        var names = string.Join(", ", unpinned.Select(c => c.Label));
        var pronoun = unpinned.Count == 1 ? "it" : "them";

        return new DiagnosisFinding(
            $"{names} — no core assigned yet",
            $"{(unpinned.Count == 1 ? "This controller is" : "These controllers are")} still using Windows' default core assignment for interrupts. Your last DPC/ISR trace shows core {quietest.CoreIndex} handling the fewest interrupts of any core ({quietest.SharePercent:0}% of them) — a good place to pin a latency-sensitive device.",
            [$"DPC/ISR: core {quietest.CoreIndex} is the quietest, at {quietest.SharePercent:0}% of all interrupts."],
            $"In the Affinity tab, pin {pronoun} to core {quietest.CoreIndex} for steadier timing.",
            FindingSeverity.Suggestion);
    }

    /// <summary>
    /// The fallback for a contended controller that WorstPortWithContention didn't already cover —
    /// either no port on it has been tested at all, or (now that that rule matches down to a 1.25x
    /// gap) its tested port genuinely isn't worse than the others. Either way this still checks the
    /// port history itself rather than defaulting to "if you haven't tested this port yet" — saying
    /// that about a port that's clearly already been tested is exactly the kind of disconnected,
    /// template-shaped answer this generator exists to avoid.
    /// </summary>
    private static DiagnosisFinding? UnresolvedContention(IReadOnlyList<PortHistoryGroup> portGroups, IReadOnlyList<ControllerSignal> controllers, List<DiagnosisFinding> alreadyFound)
    {
        // Skip only a controller WorstPortWithContention already covered — checked by its own
        // ContentionDetail text appearing in an existing finding's evidence, not just "some finding
        // somewhere mentions Affinity", which would wrongly suppress a second, unrelated contended
        // controller just because a different one was already reported.
        var contended = controllers.FirstOrDefault(c =>
            c.ContentionDetail is not null &&
            !alreadyFound.Any(f => f.Evidence.Any(e => e.Contains(c.ContentionDetail, StringComparison.Ordinal))));

        if (contended is null)
        {
            return null;
        }

        var evidence = new List<string> { $"Affinity: {contended.ContentionDetail}" };
        var testedPort = portGroups.FirstOrDefault(g => MatchesController(g.Best, contended));

        string action;
        if (testedPort?.Best.AverageJitterMs is double jitter)
        {
            evidence.Add($"Port test: {testedPort.Best.PortLabel} (on this controller) already measured {jitter:0.000} ms jitter.");
            action = $"{testedPort.Best.PortLabel} is already tested — move the shared device to a controller of its own and re-test to see whether the jitter improves.";
        }
        else
        {
            action = "Run a port test on this controller's input device to see the real impact, or move it to a controller of its own.";
        }

        return new DiagnosisFinding(
            $"Controller {contended.ControllerNumber} shares bandwidth with another device",
            contended.ContentionDetail!,
            evidence,
            action,
            FindingSeverity.Suggestion);
    }

    /// <summary>
    /// The join between a saved port result and a live controller signal. Prefers the durable
    /// InstanceId when the result has one (everything saved after this field was added); falls back
    /// to parsing "Controller N" out of PortLocation for older saved results, which is positional and
    /// can drift if a later enumeration ever counts or orders controllers differently.
    /// </summary>
    private static ControllerSignal? FindController(PortRankResult result, IReadOnlyList<ControllerSignal> controllers)
    {
        if (result.HostControllerInstanceId is { } instanceId)
        {
            var byId = controllers.FirstOrDefault(c => string.Equals(c.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        var number = ExtractControllerNumber(result.PortLocation);
        return controllers.FirstOrDefault(c => c.ControllerNumber == number);
    }

    private static bool MatchesController(PortRankResult result, ControllerSignal controller)
        => result.HostControllerInstanceId is { } instanceId
            ? string.Equals(instanceId, controller.InstanceId, StringComparison.OrdinalIgnoreCase)
            : ExtractControllerNumber(result.PortLocation) == controller.ControllerNumber;

    private static int ExtractControllerNumber(string portLocation)
    {
        var match = Regex.Match(portLocation, @"Controller (\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }
}
