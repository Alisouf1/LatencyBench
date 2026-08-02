using System.Collections.Generic;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.PortTesting;

public sealed class PortHistoryGroup
{
    public required string PortLocation { get; init; }

    public required PortRankResult Best { get; init; }

    public required IReadOnlyList<PortRankResult> History { get; init; }

    public bool IsHighFrequency => PortHistoryGrouping.IsHighFrequency(Best);
}
