namespace LatencyBench.Core.Models;

public sealed class HostControllerInfo
{
	public required string InstanceId { get; init; }

	public required string FriendlyName { get; init; }

	public required int LogicalProcessorCount { get; init; }

	public InterruptAffinityPolicy Policy { get; set; } = InterruptAffinityPolicy.MachineDefault;

	public ulong AffinityMask { get; set; }

	public InterruptPriority Priority { get; set; } = InterruptPriority.Undefined;
}
