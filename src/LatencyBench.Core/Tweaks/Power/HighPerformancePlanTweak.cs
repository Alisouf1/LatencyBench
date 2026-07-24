using System;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Power;

public sealed class HighPerformancePlanTweak : ITweak
{
	private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

	private readonly PowerCfgRunner _powerCfg;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "power.high-performance-plan",
		Category = TweakCategory.Power,
		Name = "High performance power plan",
		Description = "Switches the active Windows power plan to High performance. Revert switches back to the built-in Balanced plan.",
		Risk = TweakRisk.Safe
	};

	public HighPerformancePlanTweak(PowerCfgRunner powerCfg)
	{
		_powerCfg = powerCfg;
	}

	public TweakState GetState()
	{
		try
		{
			return (!string.Equals(_powerCfg.GetActiveSchemeGuid(), "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase)) ? TweakState.NotApplied : TweakState.Applied;
		}
		catch
		{
			return TweakState.Unknown;
		}
	}

	public void Apply()
	{
		_powerCfg.SetActiveScheme("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
	}

	public void Revert()
	{
		_powerCfg.SetActiveScheme("381b4222-f694-41f0-9685-ff5bb260df2e");
	}
}
