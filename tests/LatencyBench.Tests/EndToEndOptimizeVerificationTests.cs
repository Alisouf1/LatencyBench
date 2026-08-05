using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.SystemInfo;
using LatencyBench.Core.Tweaks;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace LatencyBench.Tests;

/// <summary>
/// Executes the complete Optimize pipeline — real analyser, real planner, real runner, real registry
/// writes — and proves each stage by reading the machine back, not by inspecting code.
///
/// It uses the "Enhance pointer precision" tweak deliberately: it is the only optimisation in the
/// catalog that lives entirely under HKEY_CURRENT_USER, so the whole apply path can be driven for
/// real without administrator rights, which is what makes end-to-end verification possible in an
/// unelevated environment at all. Every other tweak writes to HKLM and would need elevation.
///
/// This MUTATES REAL USER SETTINGS, so it is opt-in: it no-ops unless LATENCYBENCH_E2E=1. Both the
/// mouse settings and the tweak backup file are captured up front and restored in a finally, so the
/// machine is left exactly as it was found even if an assertion fails partway.
/// </summary>
[Collection("DiagnosticLog")]
public sealed class EndToEndOptimizeVerificationTests
{
    private const string MouseKeyPath = @"Control Panel\Mouse";

    private static readonly string[] MouseValues = { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" };

    private readonly ITestOutputHelper _output;

    public EndToEndOptimizeVerificationTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("LATENCYBENCH_E2E") == "1";

    private static Dictionary<string, string?> ReadMouseSettings()
    {
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(MouseKeyPath);
        foreach (string name in MouseValues)
        {
            current[name] = key?.GetValue(name) as string;
        }

        return current;
    }

    private static void WriteMouseSettings(IReadOnlyDictionary<string, string?> values)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(MouseKeyPath, writable: true);
        foreach ((string name, string? value) in values)
        {
            if (value is null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(name, value, RegistryValueKind.String);
            }
        }
    }

    [Fact]
    public async Task TheWholeOptimizePipelineProducesRealSystemChanges()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set LATENCYBENCH_E2E=1 to run. This test writes to the real registry.");
            return;
        }

        Dictionary<string, string?> original = ReadMouseSettings();
        _output.WriteLine("=== ORIGINAL MOUSE SETTINGS (will be restored) ===");
        foreach ((string name, string? value) in original)
        {
            _output.WriteLine($"  {name} = {value ?? "(absent)"}");
        }

        string backupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LatencyBench",
            "tweak-backups.json");
        string? backupContents = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;

        try
        {
            // STAGE 1 -------------------------------------------------------------------------
            // Put the machine into a genuinely un-optimised state by restoring Windows' own
            // defaults for pointer acceleration. This is what gives the analyser something real to
            // find; on this already-tuned PC every check otherwise reports "already configured".
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 1: set pointer acceleration to Windows defaults (un-optimised) ===");
            WriteMouseSettings(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MouseSpeed"] = "1",
                ["MouseThreshold1"] = "6",
                ["MouseThreshold2"] = "10",
            });

            Dictionary<string, string?> afterSeed = ReadMouseSettings();
            _output.WriteLine($"  MouseSpeed now = {afterSeed["MouseSpeed"]}");
            Assert.Equal("1", afterSeed["MouseSpeed"]);

            // STAGE 2 -------------------------------------------------------------------------
            // The real analyser must now detect it. This proves detection reads live system state
            // rather than a cached or hardcoded verdict.
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 2: real analysis must now detect pointer precision ===");
            var service = new RecommendationService(new SystemProfiler());
            RecommendationReport report = await service.AnalyzeAsync(Array.Empty<DpcIsrTestResult>());

            _output.WriteLine($"  recommendations={report.Recommendations.Count} " +
                              $"alreadySatisfied={report.NotApplicable.Count} failures={report.Failures.Count}");

            Recommendation? pointer = report.Recommendations
                .FirstOrDefault(r => r.Id == "rec.input.pointer-precision");

            Assert.True(
                pointer is not null,
                "The analyser did not detect pointer acceleration after it was deliberately turned back on. " +
                $"Found: {string.Join(", ", report.Recommendations.Select(r => r.Id))}");

            _output.WriteLine($"  DETECTED: {pointer!.Id} - {pointer.Title}");
            _output.WriteLine($"  category={pointer.Category} action={pointer.Action}");

            // STAGE 3 -------------------------------------------------------------------------
            // The three headline profiles must each produce a real, inspectable plan, and they must
            // not all be identical - that is precisely the "profiles do nothing different" claim.
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 3: per-profile plans over the SAME report ===");
            var planner = new OptimizationPlanner();
            var catalog = new ProfileCatalog();
            var stepIds = new Dictionary<ProfileKind, string[]>();

            foreach (OptimizationProfile profile in catalog.BuildAll())
            {
                OptimizationPlan plan = planner.Plan(report, profile);
                stepIds[profile.Kind] = plan.Steps.Select(s => s.Recommendation.Id).ToArray();

                _output.WriteLine($"  {profile.Name,-16} steps={plan.Steps.Count} skipped={plan.Skipped.Count}");
                foreach (PlanStep step in plan.Steps)
                {
                    _output.WriteLine($"        APPLY {step.Recommendation.Id}");
                }

                foreach (SkippedRecommendation skipped in plan.Skipped)
                {
                    _output.WriteLine($"        SKIP  {skipped.Recommendation.Id}: {skipped.Reason}");
                }
            }

            // Productivity deliberately excludes the Input category entirely, so it must NOT plan the
            // pointer fix that Competitive FPS treats as a priority. If these two ever match, profile
            // filtering has stopped working.
            Assert.Contains("rec.input.pointer-precision", stepIds[ProfileKind.CompetitiveFps]);
            Assert.DoesNotContain("rec.input.pointer-precision", stepIds[ProfileKind.Productivity]);
            _output.WriteLine("  VERIFIED: Competitive FPS plans the input fix; Productivity excludes it.");

            // STAGE 4 -------------------------------------------------------------------------
            // Apply for real through the real runner and confirm the REGISTRY changed. This is the
            // step that distinguishes "the UI said applied" from "the machine changed".
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 4: real Apply through OptimizationRunner ===");
            OptimizationPlan competitivePlan = planner.Plan(report, catalog.Get(ProfileKind.CompetitiveFps));
            OptimizationPlan inputOnly = new()
            {
                Profile = competitivePlan.Profile,
                Steps = competitivePlan.Steps
                    .Where(s => s.Recommendation.Id == "rec.input.pointer-precision")
                    .ToList(),
                Skipped = Array.Empty<SkippedRecommendation>(),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            Assert.Single(inputOnly.Steps);

            var runner = new OptimizationRunner();
            OptimizationResult result = await runner.ApplyAsync(inputOnly, createRestorePoint: false);

            _output.WriteLine($"  succeeded={result.Succeeded} applied={result.Applied.Count} " +
                              $"failed={result.Failures.Count} rolledBack={result.RolledBack}");
            foreach (StepResult stepResult in result.Results)
            {
                _output.WriteLine($"    {stepResult.Step.Recommendation.Id} => {stepResult.Outcome} {stepResult.Message}");
            }

            Assert.True(result.Succeeded, "Apply reported failure: " +
                string.Join(" | ", result.Failures.Select(f => f.Message)));

            // STAGE 5 -------------------------------------------------------------------------
            // The actual proof: read the registry directly, independently of anything the app says.
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 5: read the registry back independently ===");
            Dictionary<string, string?> afterApply = ReadMouseSettings();
            foreach ((string name, string? value) in afterApply)
            {
                _output.WriteLine($"  {name} = {value ?? "(absent)"}");
            }

            Assert.Equal("0", afterApply["MouseSpeed"]);
            Assert.Equal("0", afterApply["MouseThreshold1"]);
            Assert.Equal("0", afterApply["MouseThreshold2"]);
            _output.WriteLine("  VERIFIED: the registry genuinely changed 1/6/10 -> 0/0/0.");

            // STAGE 6 -------------------------------------------------------------------------
            // Re-analysing must now agree the work is done, proving UI state tracks real system state.
            _output.WriteLine("");
            _output.WriteLine("=== STAGE 6: re-analysis must now report it as satisfied ===");
            RecommendationReport after = await service.AnalyzeAsync(Array.Empty<DpcIsrTestResult>());

            Assert.DoesNotContain(after.Recommendations, r => r.Id == "rec.input.pointer-precision");
            Assert.Contains(after.NotApplicable, n => n.RuleId == "rec.input.pointer-precision");
            _output.WriteLine("  VERIFIED: the check now reports as already configured.");
        }
        finally
        {
            WriteMouseSettings(original);

            if (backupContents is null)
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            else
            {
                File.WriteAllText(backupPath, backupContents);
            }

            Dictionary<string, string?> restored = ReadMouseSettings();
            _output.WriteLine("");
            _output.WriteLine("=== RESTORED ===");
            foreach ((string name, string? value) in restored)
            {
                _output.WriteLine($"  {name} = {value ?? "(absent)"}");
            }
        }
    }
}
