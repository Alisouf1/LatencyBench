using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LatencyBench.App.ViewModels;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Benchmarking;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Monitoring;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises OptimizeViewModel's own orchestration — when a benchmark is captured, gated correctly
/// around a rollback, and never allowed to fail the apply it wraps — against fake tweaks and a
/// scripted counter source, on the app's WPF STA thread the view-model's ObservableObject plumbing
/// expects to run on.
/// </summary>
public class OptimizeViewModelBenchmarkTests
{
    private sealed class RecordingTweak : ITweak
    {
        private readonly bool _failOnApply;

        public RecordingTweak(string id, bool failOnApply = false)
        {
            _failOnApply = failOnApply;
            Definition = new TweakDefinition
            {
                Id = id,
                Category = TweakCategory.Power,
                Name = id,
                Description = id
            };
        }

        public TweakDefinition Definition { get; }

        public bool IsApplied { get; private set; }

        public TweakState GetState() => IsApplied ? TweakState.Applied : TweakState.NotApplied;

        public void Apply()
        {
            if (_failOnApply)
            {
                throw new InvalidOperationException($"{Definition.Id} refused to apply.");
            }

            IsApplied = true;
        }

        public void Revert() => IsApplied = false;
    }

    private sealed class FakeCatalog : TweakCatalog
    {
        private readonly IReadOnlyList<ITweak> _tweaks;

        public FakeCatalog(params ITweak[] tweaks) => _tweaks = tweaks;

        public override IReadOnlyList<ITweak> BuildAll() => _tweaks;
    }

    private sealed class NoRestorePointService : RestorePointService
    {
        public override (bool Success, string Message) CreateRestorePoint(string description) =>
            (true, "Test restore point.");
    }

    /// <summary>A fixed reading for the whole capture. BenchmarkRunner opens a fresh ICounterSource
    /// per CaptureAsync call — see its factory-per-capture comment — so a stateful "loaded then quiet"
    /// single class would restart at "loaded" for every capture rather than transitioning across the
    /// before/after boundary. The two phases are instead expressed as two fixed sources.</summary>
    private sealed class FixedCounterSource : ICounterSource
    {
        private readonly (double Interrupt, double Dpc, double Processor, double AvailableMemory) _reading;

        public FixedCounterSource((double, double, double, double) reading) => _reading = reading;

        public void Prime()
        {
        }

        public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
            => (_reading.Interrupt, _reading.Dpc, _reading.Processor);

        public double SampleAvailableMemoryPercent() => _reading.AvailableMemory;

        public void Dispose()
        {
        }
    }

    /// <summary>Returns a "loaded" source for the first capture (the baseline) and a "quiet" source
    /// for every capture after that (the post-apply measurement), so before and after are guaranteed
    /// to differ deterministically rather than depending on timing.</summary>
    private static Func<ICounterSource> LoadedThenQuietFactory()
    {
        int capturesSoFar = 0;
        return () => capturesSoFar++ == 0
            ? new FixedCounterSource((8.0, 8.0, 40.0, 70.0))
            : new FixedCounterSource((1.0, 1.0, 15.0, 70.0));
    }

    private static PlanStepViewModel FakeStep(string tweakId) => new(
        new PlanStep
        {
            Order = 1,
            Phase = ExecutionPhase.Immediate,
            Recommendation = new Recommendation
            {
                Id = $"rec.{tweakId}",
                Title = tweakId,
                Category = RecommendationCategory.Power,
                Reasoning = "test",
                ExpectedBenefit = "test",
                Risks = string.Empty,
                Evidence = new[] { new Evidence("test", "test") },
                Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.SingleDevice),
                Confidence = RecommendationConfidence.Likely,
                Action = new RecommendedAction.ApplyTweak(tweakId),
                ImpactScore = 50
            }
        },
        isSelected: true);

    private static async Task<OptimizeViewModel> BuildAndAnalyzedAsync(OptimizationRunner runner, BenchmarkRunner benchmark)
    {
        var viewModel = new OptimizeViewModel(
            new RecommendationService(new SystemProfiler()),
            new DpcIsrHistoryStore(),
            new InterruptAffinityService(),
            new InterruptDeviceService(),
            runner,
            benchmark,
            // The real 8-second default would make this suite take upwards of 16 seconds per test —
            // each capture still takes one real sample interval, just a much shorter one.
            benchmarkDuration: TimeSpan.FromMilliseconds(20));

        // _report has to be non-null before Apply will run at all; the real analysis pass sets it.
        // The plan steps that analysis produced are then discarded and replaced with the synthetic
        // one each test wants, since applying only reads from the Steps collection, never from the
        // report directly.
        await viewModel.AnalyzeCommand.ExecuteAsync(null);
        viewModel.SelectedProfile = viewModel.Profiles.Single(p => p.Kind == ProfileKind.Custom);

        return viewModel;
    }

    [Fact]
    public async Task MeasuresBeforeAndAfterASuccessfulApplyByDefault()
    {
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => new FixedCounterSource((8.0, 8.0, 40.0, 70.0)));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.BenchmarkResult);
    }

    [Fact]
    public async Task DoesNotMeasureWhenTheToggleIsOff()
    {
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => new FixedCounterSource((8.0, 8.0, 40.0, 70.0)));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.MeasureBeforeAfter = false;
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Null(viewModel.BenchmarkResult);
    }

    [Fact]
    public async Task DoesNotMeasureWhenTheApplyIsRolledBack()
    {
        // A rollback means the machine ended up unchanged, so a before/after comparison would just
        // measure "nothing happened" and present it as a result — worse than no result at all.
        var runner = new OptimizationRunner(
            new FakeCatalog(new RecordingTweak("t1"), new RecordingTweak("t2", failOnApply: true)),
            new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => new FixedCounterSource((8.0, 8.0, 40.0, 70.0)));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));
        viewModel.Steps.Add(FakeStep("t2"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Null(viewModel.BenchmarkResult);
        Assert.Contains("undone", viewModel.ResultSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedMeasurementDoesNotFailTheApply()
    {
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => throw new InvalidOperationException("counter category missing"));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.Null(viewModel.BenchmarkResult);
        Assert.Contains("t1", viewModel.Results.Select(r => r.Title));
        Assert.True(viewModel.Results.Single().Succeeded);
        Assert.Contains("measurement failed", viewModel.ResultSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResultSummaryAndResultsSurviveThePostApplyRefresh()
    {
        // RunApplyAsync always re-runs analysis at the end so the page reflects the machine's new
        // state, and that refresh reused the exact same reset code as a standalone Re-analyse. Those
        // resets used to be unconditional, so Results and ResultSummary — populated moments earlier by
        // the apply that just ran — were wiped out again before the user could see them: the "Result"
        // panel rendered for an instant and then went blank.
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => new FixedCounterSource((8.0, 8.0, 40.0, 70.0)));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.MeasureBeforeAfter = false;
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrWhiteSpace(viewModel.ResultSummary));
        Assert.Contains("t1", viewModel.Results.Select(r => r.Title));
    }

    [Fact]
    public async Task ReAnalyseAfterAnApplyClearsTheStaleResult()
    {
        // The other side of the same fix: a standalone Re-analyse (not the automatic post-apply one)
        // must still clear a previous run's result — it would otherwise show a result for the wrong,
        // no-longer-current apply.
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        var benchmark = new BenchmarkRunner(() => new FixedCounterSource((8.0, 8.0, 40.0, 70.0)));

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.MeasureBeforeAfter = false;
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ResultSummary));

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ResultSummary);
        Assert.Empty(viewModel.Results);
    }

    [Fact]
    public async Task TheComparisonHeadlineReflectsTheScriptedLoadDrop()
    {
        var runner = new OptimizationRunner(new FakeCatalog(new RecordingTweak("t1")), new NoRestorePointService());
        // First capture (the baseline) reads loaded; every capture after (the post-apply measurement)
        // reads quiet — see LoadedThenQuietFactory for why this can't be one stateful source.
        var benchmark = new BenchmarkRunner(LoadedThenQuietFactory());

        var viewModel = await BuildAndAnalyzedAsync(runner, benchmark);
        viewModel.Steps.Clear();
        viewModel.Steps.Add(FakeStep("t1"));

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.BenchmarkResult);
        Assert.Contains("improvement", viewModel.BenchmarkResult!.Headline, StringComparison.OrdinalIgnoreCase);
    }
}
