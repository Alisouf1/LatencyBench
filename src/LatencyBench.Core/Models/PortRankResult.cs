using System;

namespace LatencyBench.Core.Models;

public sealed class PortRankResult
{
	public required string PortLabel { get; init; }

	public required string PortLocation { get; init; }

	public PortRank Rank { get; init; } = PortRank.NotTested;

	public double? AverageJitterMs { get; init; }

	public double? AverageLatencyMs { get; init; }

	public int? PollingRateHz { get; init; }

	public DateTime SavedAt { get; init; } = DateTime.Now;

	public bool UnderLoad { get; init; }

	public string? HostControllerInstanceId { get; init; }
}
