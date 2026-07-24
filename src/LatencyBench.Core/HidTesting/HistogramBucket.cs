namespace LatencyBench.Core.HidTesting;

public readonly record struct HistogramBucket(string Label, int Count, double Fraction, bool IsLate);
