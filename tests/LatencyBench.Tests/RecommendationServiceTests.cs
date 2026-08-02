using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo;

namespace LatencyBench.Tests;

/// <summary>
/// End-to-end against the real machine. The engine tests cover the reasoning; these cover the
/// collection step that feeds it, which is the part that can only fail against real hardware.
/// </summary>
public class RecommendationServiceTests
{
    [Fact]
    public async Task ProducesAReportAgainstTheRealMachine()
    {
        var service = new RecommendationService(new SystemProfiler());

        RecommendationReport report = await service.AnalyzeAsync();

        Assert.Empty(report.Failures);

        // Every rule must account for itself one way or another, even against live data.
        int accounted = report.Recommendations.Count + report.NotApplicable.Count + report.Undetermined.Count;
        Assert.Equal(RecommendationEngine.DefaultRules().Count, accounted);
    }

    [Fact]
    public async Task LiveRecommendationsAreFullyExplained()
    {
        var service = new RecommendationService(new SystemProfiler());

        RecommendationReport report = await service.AnalyzeAsync();

        Assert.All(report.Recommendations, recommendation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recommendation.Reasoning));
            Assert.False(string.IsNullOrWhiteSpace(recommendation.ExpectedBenefit));
            Assert.NotEmpty(recommendation.Evidence);
        });
    }

    [Fact]
    public async Task AnalysisStaysWithinAnInteractiveBudget()
    {
        // Reading tweak state launches fsutil and opens a service handle; device enumeration walks the
        // whole tree. This has to stay fast enough to run when the user opens a tab.
        var service = new RecommendationService(new SystemProfiler());
        await service.AnalyzeAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.AnalyzeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 5000,
            $"A warm analysis took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WriteReportToTempFile()
    {
        // Same purpose as the detection dump: invariant tests catch impossible output, not
        // plausible-but-wrong output. This exists to be read.
        var service = new RecommendationService(new SystemProfiler());
        RecommendationReport report = await service.AnalyzeAsync();

        var text = new StringBuilder();
        text.AppendLine($"Generated {report.GeneratedAt:u}");
        text.AppendLine();

        text.AppendLine($"=== RECOMMENDED ({report.Recommendations.Count}) ===");
        foreach (Recommendation recommendation in report.Recommendations)
        {
            text.AppendLine();
            text.AppendLine($"[{recommendation.ImpactScore,3}] {recommendation.Title}");
            text.AppendLine($"  category   : {recommendation.Category}");
            text.AppendLine($"  confidence : {recommendation.Confidence}");
            text.AppendLine($"  safety     : {recommendation.Safety.Score}/100 {recommendation.Safety.Level} — {recommendation.Safety.Summary}");
            text.AppendLine($"  action     : {DescribeAction(recommendation.Action)}");
            text.AppendLine($"  why        : {recommendation.Reasoning}");
            text.AppendLine($"  benefit    : {recommendation.ExpectedBenefit}");
            if (!string.IsNullOrWhiteSpace(recommendation.Risks))
            {
                text.AppendLine($"  risks      : {recommendation.Risks}");
            }

            foreach (Evidence evidence in recommendation.Evidence)
            {
                text.AppendLine($"  evidence   : {evidence.Observation}  [{evidence.Source}]");
            }

            foreach (string deduction in recommendation.Safety.Deductions)
            {
                text.AppendLine($"  deduction  : {deduction}");
            }

            foreach (string blocker in recommendation.Safety.Blockers)
            {
                text.AppendLine($"  BLOCKER    : {blocker}");
            }

            foreach (RecommendationConflict conflict in recommendation.Conflicts)
            {
                text.AppendLine($"  conflict   : [{conflict.Kind}] with {conflict.OtherRecommendationId} — {conflict.Explanation}");
            }
        }

        text.AppendLine();
        text.AppendLine($"=== AUTO-APPLICABLE ({report.AutoApplicable.Count}) ===");
        foreach (Recommendation recommendation in report.AutoApplicable)
        {
            text.AppendLine($"  {recommendation.Title}");
        }

        text.AppendLine();
        text.AppendLine($"=== NOT APPLICABLE ({report.NotApplicable.Count}) ===");
        foreach (RuleOutcome.NotApplicable outcome in report.NotApplicable)
        {
            text.AppendLine($"  {outcome.Title}: {outcome.Reason}");
        }

        text.AppendLine();
        text.AppendLine($"=== UNDETERMINED ({report.Undetermined.Count}) ===");
        foreach (RuleOutcome.Undetermined outcome in report.Undetermined)
        {
            text.AppendLine($"  {outcome.Title}: {outcome.MissingInformation}");
        }

        text.AppendLine();
        text.AppendLine($"=== FAILURES ({report.Failures.Count}) ===");
        foreach (string failure in report.Failures)
        {
            text.AppendLine("  " + failure);
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "latencybench-recommendations.txt"), text.ToString());
    }

    private static string DescribeAction(RecommendedAction action) => action switch
    {
        RecommendedAction.ApplyTweak tweak => $"apply tweak '{tweak.TweakId}'",
        RecommendedAction.SetInterruptAffinity affinity =>
            $"pin {affinity.InstanceId} to logical processor(s) {string.Join(",", affinity.Cores)}",
        RecommendedAction.EnableMsiMode msi => $"enable MSI on {msi.InstanceId}",
        RecommendedAction.ManualOnly manual => $"manual — {manual.Instructions}",
        _ => action.ToString() ?? "unknown"
    };
}
