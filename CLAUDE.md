# Project context

Stack: C# / .NET, WPF (MVVM), xUnit
Projects: LatencyBench.App (UI) / LatencyBench.Core (logic) / LatencyBench.Tests
Build: dotnet build -c Release
Test:  dotnet test
Lint:  dotnet format --verify-no-changes

# Priorities (highest first)

Correctness > Security > Reliability > Maintainability > Performance > UI/UX.
UI/UX is primary when the task itself is user-facing.
Never trade stability for a marginal performance gain.

# Before changing code

- Read only what's relevant to the task, but read it fully.
- Trace every call site, dependency, and execution path the change touches.
  Never assume an API's behavior — open it.
- State the root cause before the fix. If you can't find it, say so
  instead of guessing.

# Scope

- Do the requested task only. Keep diffs small, isolated, reviewable.
- Don't rewrite working code without a clear technical justification.
- Unrelated bugs, tech debt, missing tests, or optimizations go in a
  "Recommendations" section — don't fix them unless they block the task.
- Ask first: public API changes, schema changes, new dependencies,
  architectural changes, or any diff over ~300 lines.

# Code quality

- Follow the conventions of the surrounding file.
- Explicit error handling — no silent catch.
- Deterministic resource cleanup — no leaks.
- For concurrent code, state shared-state and locking assumptions in a comment.
- Domain logic belongs in Core, never in ViewModels or code-behind.
  App references Core; Core must not reference App.
- P/Invoke and Registry writes: validate inputs, dispose handles
  deterministically, never widen elevation scope without approval.

# Verification (required before claiming done)

- Run the build, test, and lint commands above. Paste the real output.
- Never claim verification that was not performed. If you can't run it,
  say so and list exactly what needs manual checking.
- Every bug fix gets a regression test: failing before, passing after.
- Performance claims require real before/after measurements.
  Never estimate a number and present it as a measurement.

# Never

- Invent benchmark numbers or test results.
- Add dependencies without explaining why.
- Run destructive Git operations (force push, hard reset, history rewrite).
- Put secrets in code, logs, or commits.
- Apply irreversible schema or data migrations without approval.

# Documentation

- Notable architectural decisions → docs/adr/NNN-title.md
  (context, decision, alternatives, consequences).
- Update README/API docs whenever public behavior changes.

# Workflow per task

1. Restate the task and root cause in 2–4 lines.
2. List the files you'll touch and why.
3. Brief implementation plan — then proceed, unless it hits the "ask first" list.
4. Implement.
5. Verify (above).
6. Report: what changed / what was verified / remaining risks /
   Recommendations / next tasks in priority order — then stop.

Do not continue to additional tasks without approval.
Ask for clarification when requirements are ambiguous or the change
spans multiple subsystems.