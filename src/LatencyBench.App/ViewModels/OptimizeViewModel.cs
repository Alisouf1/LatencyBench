using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Diagnostics;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Benchmarking;
using LatencyBench.Core.Benchmarking.Models;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Msi;
using LatencyBench.Core.Profiles;
using LatencyBench.Core.Profiles.Models;
using LatencyBench.Core.Recommendations;
using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.App.ViewModels;

/// <summary>
/// The one-click flow: pick a profile, see exactly what it would do and why, then apply it.
/// <para>
/// Analysis and planning are always separate from applying. The plan is shown in full — including
/// what the profile refused and why — before anything is written, because "trust me" is precisely
/// the behaviour this app exists to replace.
/// </para>
/// </summary>
public sealed partial class OptimizeViewModel : ObservableObject
{
    private readonly RecommendationService _recommendationService;
    private readonly DpcIsrHistoryStore _traceHistory;
    private readonly OptimizationPlanner _planner = new();
    private readonly OptimizationRunner _runner;
    private readonly BenchmarkRunner _benchmarkRunner;

    private RecommendationReport? _report;

    public ObservableCollection<ProfileOptionViewModel> Profiles { get; } = new();

    public ObservableCollection<PlanStepViewModel> Steps { get; } = new();

    public ObservableCollection<SkippedItemViewModel> Skipped { get; } = new();

    public ObservableCollection<NonFindingViewModel> NonFindings { get; } = new();

    public ObservableCollection<StepResultViewModel> Results { get; } = new();

    [ObservableProperty]
    private ProfileOptionViewModel? _selectedProfile;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private bool _advancedMode;

    [ObservableProperty]
    private bool _createRestorePoint = true;

    /// <summary>
    /// Captures a short, automatic performance-counter measurement immediately before and after
    /// applying, so every run gets a measured result by default. On by default: it costs about
    /// 16 seconds added to Apply and needs neither the ETW kernel session (which the DPC/ISR trace
    /// needs and may already be in use) nor a human physically working the mouse or keyboard for its
    /// whole duration (which the port test needs) — the two constraints that keep those tools from
    /// running automatically here.
    /// </summary>
    [ObservableProperty]
    private bool _measureBeforeAfter = true;

    [ObservableProperty]
    private string? _planSummary;

    [ObservableProperty]
    private string? _resultSummary;

    [ObservableProperty]
    private BenchmarkComparisonViewModel? _benchmarkResult;

    /// <summary>
    /// Why Apply is unavailable, or null when it is available. An empty plan is a legitimate and
    /// common outcome — a PC that is already tuned has nothing left to change — but a greyed-out
    /// button with no stated reason reads as a broken page rather than as "you are all set", which
    /// is exactly how it was reported.
    /// </summary>
    [ObservableProperty]
    private string? _applyDisabledReason;

    /// <summary>
    /// How many checks could not reach a verdict because a measurement is missing. These are the
    /// actionable ones — running the test they name is what turns them into real recommendations —
    /// and they were previously indistinguishable from checks that genuinely found nothing.
    /// </summary>
    [ObservableProperty]
    private int _measurementPromptCount;

    /// <summary>
    /// How many checks are already in the state the selected profile wants. On a PC that has been
    /// tuned already this is the entire report, and it is the single most important number on the
    /// page: without it, "0 changes to apply" and a greyed-out Apply button look identical to a
    /// profile selector that does nothing, which is exactly how a fully-optimised machine was
    /// reported as a broken Optimize tab.
    /// </summary>
    [ObservableProperty]
    private int _satisfiedCheckCount;

    /// <summary>
    /// The plain-language verdict for the selected profile — what it will change, or confirmation
    /// that everything it manages is already in place. Always populated once an analysis exists, so
    /// switching profiles always produces a visible response even when the resulting plan is empty.
    /// </summary>
    [ObservableProperty]
    private string? _profileStatusSummary;

    /// <summary>
    /// True when the selected profile has nothing left to do and nothing failed — i.e. this PC is
    /// already tuned for it. Drives the positive styling that distinguishes "all done" from "broken".
    /// </summary>
    [ObservableProperty]
    private bool _isFullyOptimizedForProfile;

    private bool _loaded;

    /// <summary>Set for the duration of the post-apply refresh <see cref="RunApplyAsync"/> triggers,
    /// so that refresh's call into <see cref="RunAnalysisAsync"/> can be told apart from a standalone
    /// Re-analyse — the former must preserve the benchmark result it just produced, the latter is a
    /// fresh start and should clear a stale one.</summary>
    private bool _isPostApplyRefresh;

    /// <summary>How long each of the two captures runs. A constructor seam purely for tests — the
    /// real 8-second default makes an automated test of the apply flow take upwards of 16 seconds
    /// per case, which a fast unit test suite should not pay for.</summary>
    private readonly TimeSpan _benchmarkDuration;

    public OptimizeViewModel(
        RecommendationService recommendationService,
        DpcIsrHistoryStore traceHistory,
        InterruptAffinityService affinityService,
        InterruptDeviceService interruptDeviceService,
        OptimizationRunner? runner = null,
        BenchmarkRunner? benchmarkRunner = null,
        TimeSpan? benchmarkDuration = null)
    {
        // The two optional parameters exist purely as test seams — every real caller leaves them
        // null and gets the real runner wired to the real affinity/MSI services, matching the pattern
        // already used for RecommendationService, TweakCatalog and RestorePointService: a test needs
        // to drive Apply's orchestration (ordering, rollback, the benchmark gating around it) without
        // it touching the actual power plan or registry, and a scripted fake plugged in here is what
        // makes that possible without reaching into private state.
        _recommendationService = recommendationService;
        _traceHistory = traceHistory;
        _runner = runner ?? new OptimizationRunner(
            affinityService: affinityService,
            interruptDeviceService: interruptDeviceService);
        _benchmarkRunner = benchmarkRunner ?? new BenchmarkRunner();
        _benchmarkDuration = benchmarkDuration ?? BenchmarkRunner.DefaultDuration;

        foreach (OptimizationProfile profile in new ProfileCatalog().BuildAll())
        {
            Profiles.Add(new ProfileOptionViewModel(profile));
        }

        SelectedProfile = Profiles.FirstOrDefault(option => option.Kind == ProfileKind.Balanced);
    }

    public void LoadIfNeeded()
    {
        if (_loaded)
        {
            DiagnosticLog.Info("Optimize", "LoadIfNeeded: already loaded, skipping analysis.");
            return;
        }

        _loaded = true;
        DiagnosticLog.Info("Optimize", "LoadIfNeeded: first visit, starting analysis.");
        AnalyzeCommand.Execute(null);
    }

    partial void OnSelectedProfileChanged(ProfileOptionViewModel? value)
    {
        DiagnosticLog.Info(
            "Optimize",
            $"Profile selected: {value?.Name ?? "(none)"}. Report available: {_report is not null}.");

        // Re-planning from the cached report is instant, so switching profiles shows its effect
        // immediately rather than needing another analysis pass.
        if (_report is not null)
        {
            BuildPlan();
        }
        else
        {
            // Worth recording rather than silently doing nothing: this is what "switching profiles
            // does nothing" looks like from the inside when the analysis never ran.
            DiagnosticLog.Warn(
                "Optimize",
                "Profile changed but no analysis report exists yet, so no plan was built.");
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        // IsBusy is set and cleared with nothing but the try/finally between the two — previously
        // Status/Results/ResultSummary were touched before the try, which meant anything unexpected
        // in that setup (and, more concretely, ApplyAsync calling this method a second time while its
        // own IsBusy was still true) could leave IsBusy stuck true forever, with every button on this
        // page bound to it via IsEnabled and no way to recover short of restarting the app.
        IsBusy = true;
        try
        {
            await RunAnalysisAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The actual analysis work, without the IsBusy guard <see cref="AnalyzeAsync"/> has — so
    /// <see cref="ApplyAsync"/> can refresh the report after applying changes without hitting that
    /// guard's "already busy" early return. Calling the guarded, command-wrapped AnalyzeAsync() from
    /// inside ApplyAsync used to do exactly that: IsBusy was already true (ApplyAsync sets it for the
    /// whole operation), so the post-apply refresh silently no-opped on every single Apply, leaving
    /// Steps/PlanSummary/NonFindings showing whatever the last analysis said before the change went in.
    /// </summary>
    private async Task RunAnalysisAsync()
    {
        Status = "Analysing this PC…";

        // Results, ResultSummary and BenchmarkResult all describe the apply that just ran. Only a
        // fresh, standalone analysis (Re-analyse) clears them — not the refresh RunApplyAsync triggers
        // at the end of its own run, which used to wipe all three unconditionally: the "Result" panel
        // and the before/after comparison would render for an instant and then disappear as soon as
        // the post-apply refresh's Results.Clear()/ResultSummary = null ran, because nothing here told
        // that refresh apart from a real Re-analyse click.
        if (!_isPostApplyRefresh)
        {
            Results.Clear();
            ResultSummary = null;
            BenchmarkResult = null;
        }

        try
        {
            _report = await _recommendationService.AnalyzeAsync(_traceHistory.Results.ToList());

            NonFindings.Clear();
            foreach (RuleOutcome.NotApplicable outcome in _report.NotApplicable)
            {
                NonFindings.Add(new NonFindingViewModel(outcome.Title, outcome.Reason, needsMeasurement: false));
            }

            foreach (RuleOutcome.Undetermined outcome in _report.Undetermined)
            {
                NonFindings.Add(new NonFindingViewModel(outcome.Title, outcome.MissingInformation, needsMeasurement: true));
            }

            DiagnosticLog.Info(
                "Optimize",
                $"Analysis complete: {_report.Recommendations.Count} actionable, " +
                $"{_report.NotApplicable.Count} already satisfied, {_report.Undetermined.Count} undetermined, " +
                $"{_report.Failures.Count} rule failure(s).");

            foreach (string failure in _report.Failures)
            {
                DiagnosticLog.Warn("Optimize", $"Rule failure: {failure}");
            }

            BuildPlan();

            Status = _report.Failures.Count > 0
                ? $"Analysed with {_report.Failures.Count} rule failure(s): {string.Join("; ", _report.Failures)}"
                : $"Analysed {_report.Recommendations.Count + _report.NotApplicable.Count + _report.Undetermined.Count} checks.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Optimize", "Analysis threw.", ex);
            Status = $"Analysis failed: {ex.Message}";
        }
    }

    private void BuildPlan()
    {
        Steps.Clear();
        Skipped.Clear();

        MeasurementPromptCount = NonFindings.Count(nonFinding => nonFinding.NeedsMeasurement);

        SatisfiedCheckCount = NonFindings.Count(nonFinding => !nonFinding.NeedsMeasurement);

        if (_report is null || SelectedProfile is null)
        {
            HasPlan = false;
            PlanSummary = null;
            ProfileStatusSummary = null;
            IsFullyOptimizedForProfile = false;
            ApplyDisabledReason = "Run an analysis first — click Re-analyse.";
            return;
        }

        OptimizationPlan plan = _planner.Plan(_report, SelectedProfile.Profile);

        foreach (PlanStep step in plan.Steps)
        {
            // Everything is pre-selected except in Advanced mode, where the point is to choose.
            Steps.Add(new PlanStepViewModel(step, isSelected: true));
        }

        foreach (SkippedRecommendation skipped in plan.Skipped)
        {
            Skipped.Add(new SkippedItemViewModel(skipped));
        }

        HasPlan = plan.Steps.Count > 0;
        IsFullyOptimizedForProfile = plan.Steps.Count == 0 && plan.Skipped.Count == 0 && SatisfiedCheckCount > 0;
        ProfileStatusSummary = BuildProfileStatusSummary(plan);

        // ProfileStatusSummary is the single verdict for the selected profile. These two exist only
        // to add detail it does not already carry: the plan's technical shape when there IS a plan,
        // and the reason Apply is unavailable when that is not already obvious from the verdict.
        // Populating all three unconditionally stacked three paragraphs that said the same thing in
        // three different ways on an already-tuned PC, which reads as the page repeating itself
        // rather than as an answer.
        PlanSummary = plan.Steps.Count > 0 ? BuildPlanSummary(plan) : null;
        ApplyDisabledReason = plan.Steps.Count > 0 ? null : BuildMeasurementHint();

        DiagnosticLog.Info(
            "Optimize",
            $"Plan built for '{plan.Profile.Name}': {plan.Steps.Count} step(s), {plan.Skipped.Count} skipped, " +
            $"{SatisfiedCheckCount} already satisfied, {MeasurementPromptCount} awaiting measurement. " +
            $"Apply enabled: {HasPlan}.");

        foreach (PlanStep step in plan.Steps)
        {
            DiagnosticLog.Info("Optimize", $"  plan step: {step.Recommendation.Id} ({step.Phase})");
        }

        foreach (SkippedRecommendation skipped in plan.Skipped)
        {
            DiagnosticLog.Info("Optimize", $"  skipped: {skipped.Recommendation.Id} - {skipped.Reason}");
        }
    }

    /// <summary>
    /// The per-profile verdict, written so that every outcome reads as a definite answer rather than
    /// as an absence of one. Selecting a profile always changes this line, which is what makes the
    /// selector visibly responsive on a PC where no profile has anything left to change.
    /// </summary>
    private string BuildProfileStatusSummary(OptimizationPlan plan)
    {
        string profile = plan.Profile.Name;

        if (plan.Steps.Count > 0)
        {
            string alreadyInPlace = SatisfiedCheckCount > 0
                ? $" {SatisfiedCheckCount} other check(s) are already in the state this profile wants."
                : string.Empty;

            return $"{profile}: {plan.Steps.Count} change(s) ready to apply.{alreadyInPlace}";
        }

        if (plan.Skipped.Count > 0)
        {
            return $"{profile}: nothing to apply. This profile deliberately declined {plan.Skipped.Count} " +
                   $"finding(s) listed below, and the other {SatisfiedCheckCount} check(s) are already in " +
                   "the state it wants. Another profile may accept the declined ones.";
        }

        if (SatisfiedCheckCount > 0)
        {
            return $"{profile}: fully applied. All {SatisfiedCheckCount} check(s) in this analysis are already " +
                   "in the state this profile wants, so there is nothing left to change. This is the finished " +
                   "state, not an error.";
        }

        return $"{profile}: no checks reached a verdict — see the measurement prompts below.";
    }

    /// <summary>
    /// The one thing the per-profile verdict cannot say for itself: that running a specific test
    /// might turn an undecided check into a real recommendation. Null when nothing is waiting on a
    /// measurement, so an already-complete PC shows a single clean verdict rather than a paragraph
    /// qualifying it.
    /// </summary>
    private string? BuildMeasurementHint() =>
        MeasurementPromptCount == 0
            ? null
            : $"{MeasurementPromptCount} check(s) below could not reach a verdict without a measurement — " +
              "running the test each one names may surface more to do.";

    private static string BuildPlanSummary(OptimizationPlan plan)
    {
        if (plan.IsEmpty)
        {
            return plan.Skipped.Count > 0
                ? $"Nothing for the {plan.Profile.Name} profile to change — every finding was either already " +
                  $"applied or deliberately declined by this profile ({plan.Skipped.Count} listed below)."
                : "Nothing to change — this PC is already configured the way the analysis would set it.";
        }

        var parts = new List<string>
        {
            $"{plan.Steps.Count} change(s)",
            $"lowest safety score {plan.LowestSafetyScore}/100"
        };

        if (plan.RequiresDeviceRestart)
        {
            parts.Add("one or more devices will be restarted");
        }

        if (plan.RequiresReboot)
        {
            parts.Add("a Windows restart is needed before some take effect");
        }

        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy || SelectedProfile is not { } profile || _report is null)
        {
            // Apply doing nothing at all is one of the reported symptoms, so each reason it can decline
            // to run is recorded rather than being an invisible early return.
            DiagnosticLog.Warn(
                "Optimize",
                $"Apply declined: busy={IsBusy}, profileSelected={SelectedProfile is not null}, " +
                $"reportAvailable={_report is not null}.");
            return;
        }

        // In Advanced mode the user's per-item choices are the plan; otherwise the whole plan runs.
        var chosen = Steps.Where(step => !AdvancedMode || step.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            DiagnosticLog.Warn(
                "Optimize",
                $"Apply declined: no steps chosen (plan has {Steps.Count}, advancedMode={AdvancedMode}).");
            Status = "Nothing selected to apply.";
            return;
        }

        DiagnosticLog.Info(
            "Optimize",
            $"Apply starting: profile='{profile.Name}', {chosen.Count} step(s), " +
            $"restorePoint={CreateRestorePoint}, measure={MeasureBeforeAfter}.");

        IsBusy = true;
        try
        {
            await RunApplyAsync(profile, chosen);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunApplyAsync(ProfileOptionViewModel profile, List<PlanStepViewModel> chosen)
    {
        Results.Clear();
        BenchmarkResult = null;

        try
        {
            BenchmarkSnapshot? before = null;
            string? measurementNote = null;
            if (MeasureBeforeAfter)
            {
                Status = $"Measuring baseline ({_benchmarkDuration.TotalSeconds:0}s)…";
                (before, string? beforeError) = await CaptureBenchmarkSafelyAsync();
                measurementNote = beforeError;
            }

            var plan = new OptimizationPlan
            {
                Profile = profile.Profile,
                Steps = chosen
                    .Select((step, index) => new PlanStep
                    {
                        Recommendation = step.Step.Recommendation,
                        Phase = step.Step.Phase,
                        Order = index + 1
                    })
                    .ToList(),
                Skipped = Array.Empty<SkippedRecommendation>(),
                CreatedAt = DateTimeOffset.UtcNow
            };

            Status = $"Applying the {profile.Name} profile…";
            var progress = new Progress<PlanStep>(step => Status = $"Applying: {step.Title}…");
            OptimizationResult result = await _runner.ApplyAsync(plan, CreateRestorePoint, progress: progress);

            foreach (StepResult stepResult in result.Results)
            {
                Results.Add(new StepResultViewModel(stepResult));

                // The record of what actually happened to the machine, per step, including the
                // post-apply verification note when a change could not be confirmed.
                string detail = string.IsNullOrWhiteSpace(stepResult.Message) ? string.Empty : $" - {stepResult.Message}";
                DiagnosticLog.Info(
                    "Optimize",
                    $"  step result: {stepResult.Step.Recommendation.Id} => {stepResult.Outcome}{detail}");
            }

            DiagnosticLog.Info(
                "Optimize",
                $"Apply finished: succeeded={result.Succeeded}, applied={result.Applied.Count}, " +
                $"failed={result.Failures.Count}, rolledBack={result.RolledBack}, " +
                $"restorePoint={result.RestorePointCreated} ({result.RestorePointMessage}).");

            // A benchmark is only meaningful when something was actually applied and kept — comparing
            // "before" against "after a rollback" would just measure the machine being unchanged and
            // present that as a result.
            if (before is not null && result.Applied.Count > 0 && !result.RolledBack)
            {
                Status = $"Measuring result ({_benchmarkDuration.TotalSeconds:0}s)…";
                (BenchmarkSnapshot? after, string? afterError) = await CaptureBenchmarkSafelyAsync();
                measurementNote = afterError;
                if (after is not null)
                {
                    BenchmarkResult = new BenchmarkComparisonViewModel(BenchmarkComparer.Compare(before, after));
                }
            }

            ResultSummary = BuildResultSummary(result, measurementNote);
            Status = result.Succeeded ? "Done." : "Finished with problems — see below.";

            // The machine has changed, so the previous analysis is stale by definition. Calling the
            // guard-free core here — not the AnalyzeCommand-wrapped AnalyzeAsync() — is what actually
            // makes this refresh happen: see the comment on RunAnalysisAsync.
            _isPostApplyRefresh = true;
            try
            {
                await RunAnalysisAsync();
            }
            finally
            {
                _isPostApplyRefresh = false;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Optimize", "Apply threw.", ex);
            Status = $"Apply failed: {ex.Message}";
        }
    }

    /// <summary>
    /// A failed measurement must never fail the apply it is wrapped around — the profile change is
    /// the thing the user asked for, and losing it because a performance counter read failed would
    /// be a worse outcome than simply not showing a before/after result this once. The error, if any,
    /// is returned rather than written straight to Status, because Status is overwritten by the very
    /// next step and a message set there would flash and disappear before anyone could read it.
    /// </summary>
    private async Task<(BenchmarkSnapshot? Snapshot, string? Error)> CaptureBenchmarkSafelyAsync()
    {
        try
        {
            return (await _benchmarkRunner.CaptureAsync(_benchmarkDuration), null);
        }
        catch (Exception ex)
        {
            return (null, $"Before/after measurement failed: {ex.Message}");
        }
    }

    private static string BuildResultSummary(OptimizationResult result, string? measurementNote = null)
    {
        var parts = new List<string>();

        if (result.RolledBack)
        {
            parts.Add("A step failed, so everything applied in this run was undone. The machine is back " +
                      "to how it was before you clicked Apply.");
        }
        else
        {
            parts.Add($"{result.Applied.Count} change(s) applied.");
        }

        if (result.Failures.Count > 0)
        {
            parts.Add($"{result.Failures.Count} failed.");
        }

        parts.Add(result.RestorePointCreated
            ? "A System Restore point was created first."
            : $"Restore point: {result.RestorePointMessage}");

        if (result.DeviceRestartRecommended)
        {
            parts.Add("Restart the affected device from the Affinity or MSI tab for it to take effect.");
        }

        if (result.RebootRecommended)
        {
            parts.Add("Restart Windows for the remaining changes to take effect.");
        }

        if (!string.IsNullOrWhiteSpace(measurementNote))
        {
            parts.Add(measurementNote);
        }

        return string.Join(" ", parts);
    }
}

public sealed class ProfileOptionViewModel
{
    public ProfileOptionViewModel(OptimizationProfile profile) => Profile = profile;

    public OptimizationProfile Profile { get; }

    public ProfileKind Kind => Profile.Kind;

    public string Name => Profile.Name;

    public string Description => Profile.Description;
}

public sealed partial class PlanStepViewModel : ObservableObject
{
    public PlanStepViewModel(PlanStep step, bool isSelected)
    {
        Step = step;
        IsSelected = isSelected;
    }

    public PlanStep Step { get; }

    [ObservableProperty]
    private bool _isSelected;

    public Recommendation Recommendation => Step.Recommendation;

    public string Title => Step.Title;

    public string Reasoning => Recommendation.Reasoning;

    public string ExpectedBenefit => Recommendation.ExpectedBenefit;

    public string Risks => Recommendation.Risks;

    public string SafetyLabel => $"Safety {Recommendation.Safety.Score}/100 — {Recommendation.Safety.Summary}";

    public string ConfidenceLabel => Recommendation.Confidence switch
    {
        RecommendationConfidence.Measured => "Measured on this PC",
        RecommendationConfidence.Likely => "Based on documented Windows behaviour",
        _ => "Inferred from configuration"
    };

    public string PhaseLabel => Step.Phase switch
    {
        ExecutionPhase.PowerPlan => "Runs first — switches the power plan",
        ExecutionPhase.PowerPlanScoped => "Applies to the active power plan",
        ExecutionPhase.DeviceRestart => "Needs a device restart",
        ExecutionPhase.RequiresReboot => "Needs a Windows restart",
        _ => "Takes effect immediately"
    };

    public IReadOnlyList<string> EvidenceLines =>
        Recommendation.Evidence.Select(e => $"{e.Observation}  ({e.Source})").ToList();

    public IReadOnlyList<string> ConflictLines =>
        Recommendation.Conflicts.Select(c => $"{c.Kind}: {c.Explanation}").ToList();

    public bool HasRisks => !string.IsNullOrWhiteSpace(Risks);

    public bool HasConflicts => Recommendation.Conflicts.Count > 0;
}

public sealed class SkippedItemViewModel
{
    public SkippedItemViewModel(SkippedRecommendation skipped)
    {
        Title = skipped.Recommendation.Title;
        Reason = skipped.Reason;
    }

    public string Title { get; }

    public string Reason { get; }
}

public sealed class NonFindingViewModel
{
    public NonFindingViewModel(string title, string reason, bool needsMeasurement)
    {
        Title = title;
        Reason = reason;
        NeedsMeasurement = needsMeasurement;
    }

    public string Title { get; }

    public string Reason { get; }

    /// <summary>True when the rule could not decide because a measurement is missing — these are the
    /// prompts telling the user which test to run next, not merely "nothing to do".</summary>
    public bool NeedsMeasurement { get; }
}

/// <summary>Display wrapper over <see cref="BenchmarkComparisonResult"/>.</summary>
public sealed class BenchmarkComparisonViewModel
{
    public BenchmarkComparisonViewModel(BenchmarkComparisonResult result)
    {
        Headline = result.Headline;
        MetricLines = result.Metrics.Select(m => m.Summary).ToList();
        BeforeLabel = $"Before: {result.Before.SampleCount} sample(s) over {result.Before.Duration.TotalSeconds:0}s";
        AfterLabel = $"After: {result.After.SampleCount} sample(s) over {result.After.Duration.TotalSeconds:0}s";
    }

    public string Headline { get; }

    public IReadOnlyList<string> MetricLines { get; }

    public string BeforeLabel { get; }

    public string AfterLabel { get; }
}

public sealed class StepResultViewModel
{
    public StepResultViewModel(StepResult result)
    {
        Title = result.Step.Title;
        Outcome = result.Outcome.ToString();
        Message = result.Message ?? string.Empty;
        Succeeded = result.Outcome == StepOutcome.Applied;
    }

    public string Title { get; }

    public string Outcome { get; }

    public string Message { get; }

    public bool Succeeded { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}
