using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Elevation;
using LatencyBench.Core.Models;
using LatencyBench.Core.Msi;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.SystemInfo.Models;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations;

/// <summary>
/// Gathers the live state of the machine into a <see cref="RecommendationContext"/> and runs the
/// engine over it.
/// <para>
/// This is the only place that touches both the engine and the outside world. Keeping the
/// collection here rather than inside the rules is what lets every rule be tested against a
/// constructed machine — a hybrid laptop, a virtual machine, a 128-core workstation — none of which
/// is the machine the tests run on.
/// </para>
/// </summary>
public sealed class RecommendationService
{
	private readonly SystemProfiler _profiler;
	private readonly TweakCatalog _tweakCatalog;
	private readonly InterruptDeviceEnumerator _interruptDeviceEnumerator;
	private readonly RecommendationEngine _engine;

	public RecommendationService(
		SystemProfiler profiler,
		TweakCatalog? tweakCatalog = null,
		InterruptDeviceEnumerator? interruptDeviceEnumerator = null,
		RecommendationEngine? engine = null)
	{
		_profiler = profiler;
		_tweakCatalog = tweakCatalog ?? new TweakCatalog();
		_interruptDeviceEnumerator = interruptDeviceEnumerator ?? new InterruptDeviceEnumerator();
		_engine = engine ?? new RecommendationEngine();
	}

	/// <summary>
	/// Collects everything and analyses it. All of the work happens off the calling thread: reading
	/// tweak state alone launches a process for the storage checks and opens a service handle, and
	/// device enumeration walks the whole tree.
	/// </summary>
	public async Task<RecommendationReport> AnalyzeAsync(
		IReadOnlyList<DpcIsrTestResult>? traces = null,
		CancellationToken cancellationToken = default)
	{
		SystemProfile profile = await _profiler.GetAsync(cancellationToken).ConfigureAwait(false);

		return await Task.Run(
			() => _engine.Analyze(BuildContext(profile, traces ?? Array.Empty<DpcIsrTestResult>())),
			cancellationToken).ConfigureAwait(false);
	}

	private RecommendationContext BuildContext(SystemProfile profile, IReadOnlyList<DpcIsrTestResult> traces)
	{
		return new RecommendationContext
		{
			Profile = profile,
			TweakStates = ReadTweakStates(),
			InterruptDevices = ReadInterruptDevices(),
			AffinityPolicies = ReadAffinityPolicies(profile),
			DpcIsrTraces = traces,
			IsElevated = ElevationHelper.IsRunningAsAdministrator()
		};
	}

	private Dictionary<string, TweakState> ReadTweakStates()
	{
		var states = new Dictionary<string, TweakState>(StringComparer.Ordinal);

		foreach (ITweak tweak in _tweakCatalog.BuildAll())
		{
			try
			{
				states[tweak.Definition.Id] = tweak.GetState();
			}
			catch (Exception)
			{
				// A tweak that cannot report its state is Unknown, and the rules already treat Unknown
				// as "do not claim anything about this". One unreadable setting must not cost the user
				// every other recommendation.
				states[tweak.Definition.Id] = TweakState.Unknown;
			}
		}

		return states;
	}

	private IReadOnlyList<InterruptDeviceInfo> ReadInterruptDevices()
	{
		try
		{
			return _interruptDeviceEnumerator.EnumerateInterruptCapableDevices();
		}
		catch (Exception)
		{
			// Rules distinguish an empty list from a populated one and report Undetermined for it,
			// which is the honest outcome when enumeration failed.
			return Array.Empty<InterruptDeviceInfo>();
		}
	}

	private static IReadOnlyList<HostControllerInfo> ReadAffinityPolicies(SystemProfile profile)
	{
		// Read per controller from the profile rather than re-enumerating the USB tree: the profile
		// already walked it, and this only needs the policy for controllers already identified.
		var policies = new List<HostControllerInfo>(profile.UsbControllers.Count);

		try
		{
			using var service = new InterruptAffinityService();
			foreach (UsbHostControllerSummary controller in profile.UsbControllers)
			{
				try
				{
					policies.Add(service.ReadPolicy(controller.InstanceId, controller.FriendlyName));
				}
				catch (Exception)
				{
					// A single unreadable device is skipped; its absence means the affinity rule treats
					// it as unconfigured, which is the safe default.
				}
			}
		}
		catch (NotSupportedException)
		{
			// More than 64 logical processors. The affinity rule refuses on that machine anyway, so an
			// empty list is the correct input.
		}

		return policies;
	}
}
