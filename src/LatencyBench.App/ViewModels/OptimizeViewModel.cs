using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Affinity;
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

	[ObservableProperty]
	private string? _planSummary;

	[ObservableProperty]
	private string? _resultSummary;

	private bool _loaded;

	public OptimizeViewModel(
		RecommendationService recommendationService,
		DpcIsrHistoryStore traceHistory,
		InterruptAffinityService affinityService,
		InterruptDeviceService interruptDeviceService)
	{
		_recommendationService = recommendationService;
		_traceHistory = traceHistory;
		_runner = new OptimizationRunner(
			affinityService: affinityService,
			interruptDeviceService: interruptDeviceService);

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
			return;
		}

		_loaded = true;
		AnalyzeCommand.Execute(null);
	}

	partial void OnSelectedProfileChanged(ProfileOptionViewModel? value)
	{
		// Re-planning from the cached report is instant, so switching profiles shows its effect
		// immediately rather than needing another analysis pass.
		if (_report is not null)
		{
			BuildPlan();
		}
	}

	[RelayCommand]
	private async Task AnalyzeAsync()
	{
		if (IsBusy)
		{
			return;
		}

		IsBusy = true;
		Status = "Analysing this PC…";
		Results.Clear();
		ResultSummary = null;

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

			BuildPlan();

			Status = _report.Failures.Count > 0
				? $"Analysed with {_report.Failures.Count} rule failure(s): {string.Join("; ", _report.Failures)}"
				: $"Analysed {_report.Recommendations.Count + _report.NotApplicable.Count + _report.Undetermined.Count} checks.";
		}
		catch (Exception ex)
		{
			Status = $"Analysis failed: {ex.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void BuildPlan()
	{
		Steps.Clear();
		Skipped.Clear();

		if (_report is null || SelectedProfile is null)
		{
			HasPlan = false;
			PlanSummary = null;
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
		PlanSummary = BuildPlanSummary(plan);
	}

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
		if (IsBusy || SelectedProfile is null || _report is null)
		{
			return;
		}

		// In Advanced mode the user's per-item choices are the plan; otherwise the whole plan runs.
		var chosen = Steps.Where(step => !AdvancedMode || step.IsSelected).ToList();
		if (chosen.Count == 0)
		{
			Status = "Nothing selected to apply.";
			return;
		}

		IsBusy = true;
		Results.Clear();
		Status = $"Applying the {SelectedProfile.Name} profile…";

		try
		{
			var plan = new OptimizationPlan
			{
				Profile = SelectedProfile.Profile,
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

			var progress = new Progress<PlanStep>(step => Status = $"Applying: {step.Title}…");
			OptimizationResult result = await _runner.ApplyAsync(plan, CreateRestorePoint, progress: progress);

			foreach (StepResult stepResult in result.Results)
			{
				Results.Add(new StepResultViewModel(stepResult));
			}

			ResultSummary = BuildResultSummary(result);
			Status = result.Succeeded ? "Done." : "Finished with problems — see below.";

			// The machine has changed, so the previous analysis is stale by definition.
			await AnalyzeAsync();
		}
		catch (Exception ex)
		{
			Status = $"Apply failed: {ex.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	private static string BuildResultSummary(OptimizationResult result)
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
