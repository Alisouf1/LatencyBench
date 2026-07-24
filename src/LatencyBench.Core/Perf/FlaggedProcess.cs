namespace LatencyBench.Core.Perf;

public sealed record FlaggedProcess(string ProcessName, double CpuPercent, string? KnownReason);
