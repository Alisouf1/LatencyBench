using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatencyBench.App.ViewModels;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the Optimize page's "why can't I click Apply" behaviour. A fully-tuned PC legitimately
/// produces an empty plan, and Apply is correctly disabled for it — but that state was previously
/// indistinguishable from the page being broken, which is how it was reported.
/// </summary>
public sealed class OptimizeViewModelTests
{
    /// <summary>Returns whatever report the test hands it, so the plan can be driven without depending
    /// on how the machine running the test happens to be configured.</summary>
    private sealed class ScriptedRecommendationService : RecommendationService
    {
        private readonly RecommendationReport _report;

        public ScriptedRecommendationService(RecommendationReport report)
            : base(new SystemProfiler()) => _report = report;

        public override Task<RecommendationReport> AnalyzeAsync(
            IReadOnlyList<DpcIsrTestResult>? traces = null,
            CancellationToken cancellationToken = default) => Task.FromResult(_report);
    }

    private static RecommendationReport Report(
        IReadOnlyList<RuleOutcome.NotApplicable>? notApplicable = null,
        IReadOnlyList<RuleOutcome.Undetermined>? undetermined = null) => new()
        {
            Recommendations = Array.Empty<Recommendation>(),
            NotApplicable = notApplicable ?? Array.Empty<RuleOutcome.NotApplicable>(),
            Undetermined = undetermined ?? Array.Empty<RuleOutcome.Undetermined>(),
            Failures = Array.Empty<string>(),
            GeneratedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>Pointed at a throwaway path so the test never reads or writes the user's real saved
    /// trace history. The scripted service ignores the traces passed to it anyway.</summary>
    private static OptimizeViewModel Create(RecommendationReport report) => new(
        new ScriptedRecommendationService(report),
        new DpcIsrHistoryStore(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "LatencyBenchTests_" + Guid.NewGuid().ToString("N"),
            "traces.json")),
        new InterruptAffinityService(),
        new InterruptDeviceService());

    [Fact]
    public async Task AnAlreadyTunedPcExplainsWhyApplyIsDisabledRatherThanJustGreyingItOut()
    {
        var vm = Create(Report(notApplicable: new[]
        {
            new RuleOutcome.NotApplicable("rec.a", "Check A", "Already configured."),
        }));

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(vm.HasPlan);
        Assert.NotNull(vm.ApplyDisabledReason);
        Assert.Contains("not an error", vm.ApplyDisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UndeterminedChecksAreCountedAsMeasurementPromptsAndCalledOut()
    {
        // The regression: RuleOutcome.Undetermined means "I could not decide until you measure X",
        // but it was rendered in the same list, with the same styling, as checks that genuinely found
        // nothing — and NonFindingViewModel.NeedsMeasurement was never bound in the view at all.
        var vm = Create(Report(
            notApplicable: new[] { new RuleOutcome.NotApplicable("rec.a", "Check A", "Already configured.") },
            undetermined: new[]
            {
                new RuleOutcome.Undetermined("rec.b", "Check B", "Run a DPC/ISR trace first."),
                new RuleOutcome.Undetermined("rec.c", "Check C", "Run a port test first."),
            }));

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.MeasurementPromptCount);
        Assert.Equal(2, vm.NonFindings.Count(nonFinding => nonFinding.NeedsMeasurement));
        Assert.Contains("measurement", vm.ApplyDisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APcWithNothingPendingAndNothingToMeasureDoesNotMentionMeasurements()
    {
        var vm = Create(Report(notApplicable: new[]
        {
            new RuleOutcome.NotApplicable("rec.a", "Check A", "Already configured."),
        }));

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.MeasurementPromptCount);
        Assert.DoesNotContain("measurement", vm.ApplyDisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalysisAlwaysClearsIsBusySoThePageCannotStayDisabled()
    {
        // Every control on the Optimize page binds IsEnabled to !IsBusy, so an IsBusy that never
        // returns to false is indistinguishable from the whole page being broken.
        var vm = Create(Report());

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
    }
}
