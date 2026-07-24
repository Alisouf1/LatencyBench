namespace LatencyBench.App.ViewModels;

/// <summary>How much of the traced interrupt work landed on one CPU core.</summary>
public sealed class CoreInterruptLoadViewModel
{
    public required int CoreIndex { get; init; }
    public required int EventCount { get; init; }
    public required double SharePercent { get; init; }

    /// <summary>0-1 relative to the busiest core, for bar scaling.</summary>
    public required double Fraction { get; init; }

    public string Label => $"C{CoreIndex}";
}
