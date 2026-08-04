using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.Benchmarking.Models;

/// <summary>
/// A short burst of monitoring samples, reduced to the statistics a before/after comparison needs.
/// <para>
/// Average is what a sustained change in behaviour looks like; peak is what a spike looks like. A
/// tweak can lower one without moving the other — disabling a chatty driver's interrupts lowers the
/// average, while fixing a single misbehaving one mostly lowers the peak — so both are kept rather
/// than collapsing to one number.
/// </para>
/// </summary>
public sealed record BenchmarkSnapshot(
    DateTimeOffset CapturedAt,
    TimeSpan Duration,
    int SampleCount,
    double AverageDpcTimePercent,
    double PeakDpcTimePercent,
    double AverageInterruptTimePercent,
    double PeakInterruptTimePercent,
    double AverageProcessorTimePercent,
    double AverageAvailableMemoryPercent)
{
    public static BenchmarkSnapshot FromReadings(
        DateTimeOffset capturedAt,
        TimeSpan duration,
        IReadOnlyList<(double Interrupt, double Dpc, double Processor, double AvailableMemory)> readings)
    {
        if (readings.Count == 0)
        {
            throw new ArgumentException("At least one reading is required to build a snapshot.", nameof(readings));
        }

        return new BenchmarkSnapshot(
            capturedAt,
            duration,
            readings.Count,
            AverageDpcTimePercent: readings.Average(r => r.Dpc),
            PeakDpcTimePercent: readings.Max(r => r.Dpc),
            AverageInterruptTimePercent: readings.Average(r => r.Interrupt),
            PeakInterruptTimePercent: readings.Max(r => r.Interrupt),
            AverageProcessorTimePercent: readings.Average(r => r.Processor),
            AverageAvailableMemoryPercent: readings.Average(r => r.AvailableMemory));
    }
}
