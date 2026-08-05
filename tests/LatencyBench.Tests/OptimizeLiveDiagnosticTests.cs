using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Runs the real analyser and planner against the machine executing the tests, and prints what each
/// profile would actually do. Unlike the scripted tests, this depends on how this particular PC is
/// configured, so it asserts only on invariants that must hold on ANY machine — the detail is
/// written to test output for diagnosis.
///
/// This exists because "switching profiles does nothing" and "Apply does nothing" are both the
/// expected, correct behaviour when the analyser finds nothing to change, and there was no way to
/// tell that apart from a genuine failure without seeing the real numbers.
/// </summary>
public sealed class OptimizeLiveDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public OptimizeLiveDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealAnalysisAndEveryProfilePlanAreReported()
    {
        var service = new RecommendationService(new SystemProfiler());
        RecommendationReport report = await service.AnalyzeAsync(Array.Empty<DpcIsrTestResult>());

        _output.WriteLine("=== REAL ANALYSIS ON THIS MACHINE ===");
        _output.WriteLine($"Recommendations : {report.Recommendations.Count}");
        _output.WriteLine($"NotApplicable   : {report.NotApplicable.Count}");
        _output.WriteLine($"Undetermined    : {report.Undetermined.Count}");
        _output.WriteLine($"Rule failures   : {report.Failures.Count}");

        foreach (string failure in report.Failures)
        {
            _output.WriteLine($"  RULE FAILURE: {failure}");
        }

        _output.WriteLine("");
        _output.WriteLine("--- Recommendations found ---");
        foreach (Recommendation rec in report.Recommendations)
        {
            _output.WriteLine($"  [{rec.Id}] {rec.Title}");
            _output.WriteLine($"      category={rec.Category} safety={rec.Safety.Level}/{rec.Safety.Score} " +
                              $"reboot={rec.RequiresReboot} impact={rec.ImpactScore} action={rec.Action}");
        }

        _output.WriteLine("");
        _output.WriteLine("--- Not applicable (already configured / irrelevant) ---");
        foreach (RuleOutcome.NotApplicable na in report.NotApplicable)
        {
            _output.WriteLine($"  [{na.RuleId}] {na.Title}: {na.Reason}");
        }

        _output.WriteLine("");
        _output.WriteLine("--- Undetermined (needs a measurement) ---");
        foreach (RuleOutcome.Undetermined u in report.Undetermined)
        {
            _output.WriteLine($"  [{u.RuleId}] {u.Title}: {u.MissingInformation}");
        }

        var planner = new OptimizationPlanner();
        _output.WriteLine("");
        _output.WriteLine("=== PER-PROFILE PLANS ===");

        var planned = new Dictionary<ProfileKind, int>();
        foreach (OptimizationProfile profile in new ProfileCatalog().BuildAll())
        {
            OptimizationPlan plan = planner.Plan(report, profile);
            planned[profile.Kind] = plan.Steps.Count;

            _output.WriteLine($"{profile.Name,-16} steps={plan.Steps.Count,-3} skipped={plan.Skipped.Count}");
            foreach (PlanStep step in plan.Steps)
            {
                _output.WriteLine($"      APPLY  {step.Recommendation.Id} ({step.Phase})");
            }

            foreach (SkippedRecommendation skipped in plan.Skipped)
            {
                _output.WriteLine($"      SKIP   {skipped.Recommendation.Id}: {skipped.Reason}");
            }
        }

        // Invariant that must hold on any machine: the analyser has to actually reach a verdict on
        // something. A report where every single rule failed would mean the analysis layer is broken
        // regardless of how this particular PC is configured.
        int totalVerdicts = report.Recommendations.Count + report.NotApplicable.Count + report.Undetermined.Count;
        Assert.True(
            totalVerdicts > 0,
            $"The analyser produced no verdicts at all ({report.Failures.Count} rule failures) - the analysis layer is broken.");

        Assert.True(
            report.Failures.Count == 0,
            $"Rules threw instead of returning a verdict: {string.Join(" | ", report.Failures)}");

        // Custom applies no filtering, so it is the upper bound: if anything is actionable at all on
        // this machine, Custom must plan it. This is what proves the planner is not silently dropping
        // everything - without depending on this PC needing any particular tweak.
        if (report.Recommendations.Count > 0)
        {
            Assert.True(
                planned[ProfileKind.Custom] > 0,
                "The analyser found actionable recommendations but the Custom profile (which filters nothing) planned zero steps - the planner is dropping everything.");
        }
    }
}
