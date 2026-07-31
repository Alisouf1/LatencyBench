using System;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Power;

public sealed class HighPerformancePlanTweak : ITweak
{
	private const string BackupKey = "powercfg-active-scheme";

	private readonly PowerCfgRunner _powerCfg;

	private readonly TweakBackupStore _backupStore;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "power.high-performance-plan",
		Category = TweakCategory.Power,
		Name = "High performance power plan",
		Description = "Switches the active Windows power plan to High performance. Revert restores whichever plan was active beforehand.",
		Risk = TweakRisk.Safe
	};

	public HighPerformancePlanTweak(PowerCfgRunner powerCfg, TweakBackupStore backupStore)
	{
		_powerCfg = powerCfg;
		_backupStore = backupStore;
	}

	public TweakState GetState()
	{
		try
		{
			return string.Equals(_powerCfg.GetActiveSchemeGuid(), PowerCfgRunner.SchemeHighPerformance, StringComparison.OrdinalIgnoreCase)
				? TweakState.Applied
				: TweakState.NotApplied;
		}
		catch (Exception)
		{
			return TweakState.Unknown;
		}
	}

	public void Apply()
	{
		// Windows 11 hides the High performance scheme on many OEM and modern-standby systems. Say so
		// plainly instead of letting powercfg fail with an opaque exit code.
		if (!_powerCfg.SchemeExists(PowerCfgRunner.SchemeHighPerformance))
		{
			throw new InvalidOperationException(
				"This PC does not expose the High performance power plan. On modern-standby systems Windows " +
				"replaces it with the Power mode slider, and the processor settings LatencyBench tunes apply " +
				"to the active plan directly.");
		}

		string previous = _powerCfg.GetActiveSchemeGuid();
		if (!string.Equals(previous, PowerCfgRunner.SchemeHighPerformance, StringComparison.OrdinalIgnoreCase))
		{
			// Record the plan that was actually active. Reverting to a hardcoded Balanced would destroy
			// an Ultimate Performance or OEM plan the user had deliberately selected.
			_backupStore.Save(BackupKey, previous);
		}

		_powerCfg.SetActiveScheme(PowerCfgRunner.SchemeHighPerformance);
		Verify(PowerCfgRunner.SchemeHighPerformance);
	}

	public void Revert()
	{
		string? previous = _backupStore.TryGet(BackupKey);
		if (previous is null)
		{
			// High performance was already active before LatencyBench touched anything, so there is no
			// prior plan to restore and switching to some assumed default would be a change the user
			// never asked for.
			return;
		}

		if (!_powerCfg.SchemeExists(previous))
		{
			throw new InvalidOperationException(
				$"The power plan that was active before ({previous}) no longer exists on this PC, so it cannot be restored.");
		}

		_powerCfg.SetActiveScheme(previous);
		Verify(previous);
		_backupStore.Remove(BackupKey);
	}

	private void Verify(string expectedSchemeGuid)
	{
		if (!string.Equals(_powerCfg.GetActiveSchemeGuid(), expectedSchemeGuid, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"powercfg reported success but the active power plan is still not {expectedSchemeGuid}. " +
				"A group policy or OEM power utility is most likely overriding it.");
		}
	}
}
