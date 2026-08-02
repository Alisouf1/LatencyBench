using System;

namespace LatencyBench.Core.Monitoring;

public interface ICounterSource : IDisposable
{
    /// <summary>
    /// Warms up the underlying rate counters. Windows' "% Interrupt Time" and "% DPC Time" counters
    /// are computed from the delta between two samples, so the first read after opening them is
    /// meaningless and must be discarded rather than reported as a real 0%.
    /// </summary>
    void Prime();

    (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu();

    double SampleAvailableMemoryPercent();
}
