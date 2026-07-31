using LatencyBench.Core.Recommendations.Models;

namespace LatencyBench.Core.Recommendations;

/// <summary>
/// One piece of reasoning about one thing.
/// <para>
/// Rules are deliberately not allowed to be silent. Returning
/// <see cref="RuleOutcome.NotApplicable"/> with a reason is a first-class result, because the
/// question "why is it not suggesting X?" is asked at least as often as "why is it suggesting Y?",
/// and an engine that cannot answer the first one reads as arbitrary.
/// </para>
/// </summary>
public interface IRecommendationRule
{
	/// <summary>Stable identifier, also used as the recommendation id and in conflict declarations.</summary>
	string Id { get; }

	/// <summary>Short human-readable name, shown even when the rule does not fire.</summary>
	string Title { get; }

	RuleOutcome Evaluate(RecommendationContext context);
}
