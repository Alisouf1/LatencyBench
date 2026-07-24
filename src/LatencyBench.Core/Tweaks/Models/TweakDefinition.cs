namespace LatencyBench.Core.Tweaks.Models;

public sealed class TweakDefinition
{
	public required string Id { get; init; }

	public required TweakCategory Category { get; init; }

	public required string Name { get; init; }

	public required string Description { get; init; }

	public TweakRisk Risk { get; init; } = TweakRisk.Safe;

	public bool RequiresReboot { get; init; }
}
