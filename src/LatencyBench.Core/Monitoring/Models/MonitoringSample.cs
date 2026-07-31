using System;

namespace LatencyBench.Core.Monitoring.Models;

/// <summary>One reading of the machine's latency-relevant load.</summary>
public sealed record MonitoringSample(
	DateTimeOffset Timestamp,
	double InterruptTimePercent,
	double DpcTimePercent,
	double ProcessorTimePercent,
	double AvailableMemoryPercent);

public enum WarningSeverity
{
	Info,
	Warning,
	Critical
}

public enum WarningMetric
{
	DpcTime,
	InterruptTime,
	MemoryPressure
}

/// <summary>
/// One state change in a monitored metric — either it crossed into a concerning range, or it
/// recovered out of one. Both are reported: a warning that never says when things got better trains
/// the user to ignore the panel.
/// </summary>
public sealed record MonitoringWarning(
	DateTimeOffset Timestamp,
	WarningMetric Metric,
	WarningSeverity Severity,
	string Message,
	bool IsRecovery);
