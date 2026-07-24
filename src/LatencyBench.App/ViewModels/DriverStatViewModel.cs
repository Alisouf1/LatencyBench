namespace LatencyBench.App.ViewModels;

public sealed class DriverStatViewModel
{
    public required string DriverName { get; init; }
    public required double MaxDurationMicroseconds { get; init; }
    public required int SampleCount { get; init; }
}
