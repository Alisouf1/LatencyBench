namespace LatencyBench.Core.MouseTesting.Models;

public readonly record struct MouseSample(long TimestampTicks, int Dx, int Dy, ushort ButtonFlags);
