using System;
using System.Text.Json.Serialization;

namespace LatencyBench.Core.Models;

public sealed class PortRankResult
{
    public required string PortLabel { get; init; }

    public required string PortLocation { get; init; }

    public PortRank Rank { get; init; } = PortRank.NotTested;

    public double? AverageJitterMs { get; init; }

    /// <summary>
    /// Median time between consecutive received reports — a report-spacing measurement, not
    /// end-to-end input latency (see JitterLatencyAnalyzer.AnalysisResult.MedianReportIntervalMs).
    /// The JSON key stays "AverageLatencyMs" so saved history files from before this rename still
    /// deserialize correctly.
    /// </summary>
    [JsonPropertyName("AverageLatencyMs")]
    public double? AverageReportIntervalMs { get; init; }

    public int? PollingRateHz { get; init; }

    public DateTime SavedAt { get; init; } = DateTime.Now;

    public bool UnderLoad { get; init; }

    public string? HostControllerInstanceId { get; init; }
}
