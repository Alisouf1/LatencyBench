using System;

namespace LatencyBench.Core.DpcIsr;

public sealed class DpcIsrSample
{
	public required DateTime Timestamp { get; init; }

	public required DpcIsrKind Kind { get; init; }

	public required double DurationMicroseconds { get; init; }

	public string DriverName { get; init; } = "unknown";

	public required int ProcessorNumber { get; init; }
}
