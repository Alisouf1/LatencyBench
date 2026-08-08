# ADR 002 — Input is a first-class recommendation category

**Status:** Accepted
**Date:** 2026-08-06

## Context

`RecommendationCategory` had no value for mouse and keyboard behaviour. `PointerPrecisionRule`
therefore filed itself under `System`, the nearest available bucket.

Optimization profiles filter by category. `Productivity` accepts `System` because it wants the
timer, scheduler and memory checks — so it silently accepted an input tweak that its own
description says it skips ("Skips everything whose benefit is worst-case latency you would not
notice in a document or a browser").

On an otherwise-tuned machine, pointer acceleration was the only actionable finding. The
result: **all six profiles produced an identical single-step plan.** From the UI that is
indistinguishable from a profile selector that is not wired up, and it was reported as exactly
that.

## Decision

Add `RecommendationCategory.Input` and assign it to `PointerPrecisionRule`.

## Alternatives considered

**Give Productivity an explicit exclusion for `rec.input.pointer-precision`.** Rejected: it
fixes one rule by name while leaving the taxonomy wrong, so the next input rule reintroduces
the bug. Exclusions are for "this profile declines this specific trade-off", not for papering
over a missing category.

**Remove `System` from Productivity's category set.** Rejected: it would also drop the timer,
scheduler and memory checks, which Productivity does want.

## Consequences

- Profiles built with `Set(AllCategories)` pick up `Input` automatically via `Enum.GetValues`,
  so Balanced, Gaming, Competitive FPS, Streaming and Custom needed no edit.
- Productivity lists its categories explicitly and now correctly excludes input work.
- There are no `switch` statements over `RecommendationCategory`, so extending the enum is
  safe — checked before adding the member.
- Any future input-related rule inherits the correct filtering without further changes.

## Verification

Measured end-to-end against the real registry, with pointer acceleration deliberately
re-enabled so the analyser had something to find:

| | Before | After |
|---|---|---|
| Balanced / Gaming / Competitive / Streaming / Custom | 1 step | 1 step |
| Productivity | 1 step | **0 steps, skipped: "does not change Input settings"** |

`tests/ProfileDifferentiationTests.cs` guards this permanently, including an invariant that
every category is accepted by at least one profile — a category no profile accepts would make
its recommendations impossible to apply through the Optimize tab.
