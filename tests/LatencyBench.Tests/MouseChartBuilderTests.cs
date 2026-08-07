using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the mouse chart geometry. Everything here ends up as WPF coordinates, and WPF does not
/// reject a NaN or infinite coordinate loudly - it silently fails to draw, or throws deep inside the
/// render pass where the cause is unrecoverable from the stack trace. So the invariant that matters
/// most is that every number leaving this class is finite and inside the requested canvas.
/// </summary>
public sealed class MouseChartBuilderTests
{
    private const double TicksPerMs = 10_000.0;
    private const double Width = 800.0;
    private const double Height = 400.0;

    private static List<MouseSample> Samples(params (int Dx, int Dy)[] deltas)
    {
        var list = new List<MouseSample>(deltas.Length);
        long ticks = 0;
        foreach ((int dx, int dy) in deltas)
        {
            list.Add(new MouseSample(ticks, dx, dy, 0));
            ticks += (long)TicksPerMs;
        }

        return list;
    }

    private static List<MouseSample> Ramp(int count) =>
        Samples(Enumerable.Range(0, count).Select(i => (i, -i)).ToArray());

    private static void AssertAllFinite(MouseChartBuilder.ChartData data)
    {
        Assert.All(data.Points, p =>
        {
            Assert.True(double.IsFinite(p.X), $"X was {p.X}");
            Assert.True(double.IsFinite(p.Y), $"Y was {p.Y}");
        });

        Assert.All(data.XTicks, t => Assert.True(double.IsFinite(t.Position), $"x tick was {t.Position}"));
        Assert.All(data.YTicks, t => Assert.True(double.IsFinite(t.Position), $"y tick was {t.Position}"));
    }

    private static void AssertWithinCanvas(MouseChartBuilder.ChartData data)
    {
        Assert.All(data.Points, p =>
        {
            Assert.InRange(p.X, 0.0, Width);
            Assert.InRange(p.Y, 0.0, Height);
        });
    }

    // --- Guard conditions ------------------------------------------------------------------------

    [Fact]
    public void NoSamplesProducesNoChart()
    {
        Assert.Null(MouseChartBuilder.Build(
            Array.Empty<MouseSample>(), MouseChartMode.PathXY, Width, Height, TicksPerMs));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void ANonPositiveCanvasProducesNoChart(double width, double height)
    {
        Assert.Null(MouseChartBuilder.Build(Ramp(10), MouseChartMode.PathXY, width, height, TicksPerMs));
    }

    /// <summary>
    /// The regression this pins. Build accepted any tick rate, including zero, while its sibling
    /// MouseMotionAnalyzer rejects a non-positive rate outright. A zero rate makes the first sample's
    /// X coordinate 0/0 = NaN, and NaN then propagates through Min/Max - where every comparison is
    /// false - into the pixel mapping, handing WPF NaN coordinates.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ANonPositiveTickRateProducesNoChartRatherThanNaNCoordinates(double ticksPerMs)
    {
        var result = MouseChartBuilder.Build(Ramp(10), MouseChartMode.XCountsVsTime, Width, Height, ticksPerMs);

        Assert.Null(result);
    }

    [Fact]
    public void ANonPositiveTickRateAlsoProducesNoOverlay()
    {
        Assert.Null(MouseChartBuilder.BuildOverlay(
            Ramp(10), Ramp(10), MouseChartMode.XCountsVsTime, Width, Height, 0.0));
    }

    // --- Output is always drawable ---------------------------------------------------------------

    [Theory]
    [InlineData(MouseChartMode.XCountsVsTime)]
    [InlineData(MouseChartMode.YCountsVsTime)]
    [InlineData(MouseChartMode.PathXY)]
    public void EveryModeProducesFinitePointsInsideTheCanvas(MouseChartMode mode)
    {
        var data = MouseChartBuilder.Build(Ramp(50), mode, Width, Height, TicksPerMs);

        Assert.NotNull(data);
        Assert.Equal(50, data!.Value.Points.Count);
        AssertAllFinite(data.Value);
        AssertWithinCanvas(data.Value);
    }

    [Fact]
    public void ASingleSampleDoesNotDivideByAZeroRange()
    {
        // One point means min == max on both axes; the bounds have to be widened or the mapping
        // divides by zero.
        var data = MouseChartBuilder.Build(Samples((5, 5)), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        Assert.NotNull(data);
        AssertAllFinite(data!.Value);
        AssertWithinCanvas(data.Value);
    }

    [Fact]
    public void AllIdenticalSamplesDoNotDivideByAZeroRange()
    {
        var data = MouseChartBuilder.Build(
            Samples(Enumerable.Repeat((0, 0), 20).ToArray()), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        Assert.NotNull(data);
        AssertAllFinite(data!.Value);
        AssertWithinCanvas(data.Value);
    }

    [Fact]
    public void ExtremeDeltasStayFinite()
    {
        var data = MouseChartBuilder.Build(
            Samples((int.MaxValue, int.MinValue), (int.MinValue, int.MaxValue), (0, 0)),
            MouseChartMode.PathXY, Width, Height, TicksPerMs);

        Assert.NotNull(data);
        AssertAllFinite(data!.Value);
        AssertWithinCanvas(data.Value);
    }

    // --- Geometry --------------------------------------------------------------------------------

    [Fact]
    public void TheYAxisIsInvertedBecauseScreenCoordinatesGrowDownward()
    {
        // A larger data value must map to a SMALLER pixel Y, or every chart is drawn upside down.
        var data = MouseChartBuilder.Build(
            Samples((0, 0), (0, 100)), MouseChartMode.YCountsVsTime, Width, Height, TicksPerMs);

        Assert.NotNull(data);
        Assert.True(data!.Value.Points[1].Y < data.Value.Points[0].Y,
            "the larger Y value should sit higher on screen");
    }

    [Fact]
    public void TimeIncreasesLeftToRight()
    {
        var data = MouseChartBuilder.Build(Ramp(10), MouseChartMode.XCountsVsTime, Width, Height, TicksPerMs);

        var xs = data!.Value.Points.Select(p => p.X).ToList();
        Assert.Equal(xs.OrderBy(x => x).ToList(), xs);
    }

    [Fact]
    public void PathModeAccumulatesPositionRatherThanPlottingRawDeltas()
    {
        // Three steps of (10,0) trace a line to x=30, not three identical points.
        var data = MouseChartBuilder.Build(
            Samples((10, 0), (10, 0), (10, 0)), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        var xs = data!.Value.Points.Select(p => p.X).ToList();
        Assert.True(xs[0] < xs[1] && xs[1] < xs[2], "path mode must accumulate");
    }

    [Fact]
    public void TheMarginIsHonouredOnBothAxes()
    {
        // 8% of each dimension, so nothing is drawn hard against the edge.
        var data = MouseChartBuilder.Build(Ramp(30), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        double marginX = Width * 0.08;
        double marginY = Height * 0.08;

        Assert.All(data!.Value.Points, p =>
        {
            Assert.InRange(p.X, marginX - 0.001, Width - marginX + 0.001);
            Assert.InRange(p.Y, marginY - 0.001, Height - marginY + 0.001);
        });
    }

    [Fact]
    public void ZeroIsAlwaysInsideTheVerticalRange()
    {
        // Bounds are clamped to include zero so the baseline is visible even when all movement is in
        // one direction.
        var data = MouseChartBuilder.Build(
            Samples((0, 50), (0, 60), (0, 70)), MouseChartMode.YCountsVsTime, Width, Height, TicksPerMs);

        double[] tickLabels = data!.Value.YTicks.Select(t => double.Parse(t.Label)).ToArray();
        Assert.True(tickLabels.Min() <= 0.0, $"lowest y tick was {tickLabels.Min()}, expected <= 0");
    }

    // --- Ticks -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(11)]
    public void TheRequestedNumberOfTicksIsProduced(int tickCount)
    {
        var data = MouseChartBuilder.Build(
            Ramp(20), MouseChartMode.PathXY, Width, Height, TicksPerMs, tickCount);

        Assert.Equal(tickCount, data!.Value.XTicks.Count);
        Assert.Equal(tickCount, data.Value.YTicks.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-5)]
    public void ADegenerateTickCountFallsBackToTwoRatherThanDividingByZero(int tickCount)
    {
        // tickCount - 1 is the divisor, so 1 would divide by zero and 0 would invert the axis.
        var data = MouseChartBuilder.Build(
            Ramp(20), MouseChartMode.PathXY, Width, Height, TicksPerMs, tickCount);

        Assert.NotNull(data);
        Assert.Equal(2, data!.Value.XTicks.Count);
        AssertAllFinite(data.Value);
    }

    [Fact]
    public void TickLabelsAreParseableNumbers()
    {
        var data = MouseChartBuilder.Build(Ramp(40), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        Assert.All(data!.Value.XTicks, t =>
            Assert.True(double.TryParse(t.Label, out _), $"unparseable label '{t.Label}'"));
    }

    // --- Overlay ---------------------------------------------------------------------------------

    [Fact]
    public void OverlayPutsBothSeriesOnOneSharedScale()
    {
        // The point of an overlay is comparability: a value appearing in both series must land at the
        // same pixel, which only holds if both were mapped against the combined bounds.
        var small = Samples((0, 10), (0, 20));
        var large = Samples((0, 10), (0, 1000));

        var overlay = MouseChartBuilder.BuildOverlay(
            small, large, MouseChartMode.YCountsVsTime, Width, Height, TicksPerMs);

        Assert.NotNull(overlay);
        Assert.Equal(overlay!.Value.A.Points[0].Y, overlay.Value.B.Points[0].Y, 6);
    }

    [Fact]
    public void OverlayWorksWhenOneSideIsEmpty()
    {
        var overlay = MouseChartBuilder.BuildOverlay(
            Ramp(10), Array.Empty<MouseSample>(), MouseChartMode.PathXY, Width, Height, TicksPerMs);

        Assert.NotNull(overlay);
        Assert.Equal(10, overlay!.Value.A.Points.Count);
        Assert.Empty(overlay.Value.B.Points);
        AssertAllFinite(overlay.Value.A);
        AssertAllFinite(overlay.Value.B);
    }

    [Fact]
    public void OverlayWithBothSidesEmptyProducesNothing()
    {
        Assert.Null(MouseChartBuilder.BuildOverlay(
            Array.Empty<MouseSample>(), Array.Empty<MouseSample>(),
            MouseChartMode.PathXY, Width, Height, TicksPerMs));
    }

    [Fact]
    public void OverlayPointsStayInsideTheCanvas()
    {
        var overlay = MouseChartBuilder.BuildOverlay(
            Ramp(60), Samples((0, -500), (0, 500)), MouseChartMode.YCountsVsTime, Width, Height, TicksPerMs);

        Assert.NotNull(overlay);
        AssertWithinCanvas(overlay!.Value.A);
        AssertWithinCanvas(overlay.Value.B);
    }

    // --- Performance -----------------------------------------------------------------------------

    [Fact]
    public void ALargeCaptureChartsQuickly()
    {
        // This runs on the UI thread whenever the chart mode changes or the window resizes.
        var samples = Ramp(100_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var data = MouseChartBuilder.Build(samples, MouseChartMode.PathXY, Width, Height, TicksPerMs);
        stopwatch.Stop();

        Assert.NotNull(data);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"charting 100k samples took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }
}
