using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.HidTesting.Models;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations;

/// <summary>
/// Everything a rule is allowed to look at. Rules never touch the registry or enumerate devices
/// themselves — they receive a snapshot and return a verdict, which is what makes them
/// deterministic and testable against constructed machines rather than only against the developer's.
/// </summary>
public sealed class RecommendationContext
{
	public required SystemProfile Profile { get; init; }

	/// <summary>Current state of every tweak in the catalog, keyed by tweak id.</summary>
	public required IReadOnlyDictionary<string, TweakState> TweakStates { get; init; }

	/// <summary>Interrupt-capable devices and their MSI/priority state.</summary>
	public IReadOnlyList<InterruptDeviceInfo> InterruptDevices { get; init; } = Array.Empty<InterruptDeviceInfo>();

	/// <summary>Current interrupt affinity policy per device.</summary>
	public IReadOnlyList<HostControllerInfo> AffinityPolicies { get; init; } = Array.Empty<HostControllerInfo>();

	/// <summary>Saved DPC/ISR traces, newest first is not assumed — rules that care sort explicitly.</summary>
	public IReadOnlyList<DpcIsrTestResult> DpcIsrTraces { get; init; } = Array.Empty<DpcIsrTestResult>();

	/// <summary>Saved port tests.</summary>
	public IReadOnlyList<HidTestResult> PortTests { get; init; } = Array.Empty<HidTestResult>();

	/// <summary>True when the process can actually write the settings a recommendation would apply.
	/// Rules still produce recommendations without it — the user should see what is available — but
	/// the engine marks them as needing elevation.</summary>
	public bool IsElevated { get; init; }

	public TweakState StateOf(string tweakId) =>
		TweakStates.TryGetValue(tweakId, out TweakState state) ? state : TweakState.Unknown;

	/// <summary>The most recent trace, or null when nothing has been measured yet.</summary>
	public DpcIsrTestResult? LatestTrace =>
		DpcIsrTraces.Count == 0 ? null : DpcIsrTraces.OrderByDescending(trace => trace.SavedAt).First();

	public InterruptDeviceInfo? FindInterruptDevice(string instanceId) =>
		InterruptDevices.FirstOrDefault(device =>
			string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
}
