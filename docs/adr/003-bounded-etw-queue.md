# ADR 003 — Bound the ETW sample queue and report what was dropped

**Status:** Accepted
**Date:** 2026-08-06

## Context

DPC/ISR tracing hands samples from the ETW processing thread to the UI through a
`ConcurrentQueue`, drained by a 150 ms `DispatcherTimer`.

The queue was unbounded. The producer runs at kernel interrupt rates — measured at roughly
1,500 samples/second on an idle machine here, and far higher under the load someone would
actually be tracing. Any UI stall (a modal dialog, a slow render, work on another tab) lets
the backlog grow without limit. At roughly 64 bytes per sample that is an out-of-memory path
on precisely the busy machine the tool exists to diagnose.

`Start()` also reset every accumulator except the queue itself, so a late callback arriving
after the previous trace's final drain was counted against the next trace.

## Decision

Cap the queue at 200,000 samples (~13 MB, about four seconds of headroom at 50k events/s).
When it is full, drop the incoming sample, count it, and **report the count to the user**.

`Start()` clears the queue, the depth counter and the drop counter along with the other
accumulators.

## Alternatives considered

**Block the producer until the consumer catches up.** Rejected outright: the producer is an
ETW callback running in the kernel-tracing path. Blocking it stalls event delivery
system-wide and would corrupt the very measurement being taken.

**Drop the oldest sample instead of the newest.** Rejected: it costs an extra dequeue on the
hot path for no diagnostic gain. Once the queue is saturated the trace is already
unrepresentative; which end is discarded does not change that, and saying so is what matters.

**Raise the drain rate instead of capping.** Rejected: it narrows the window without closing
it, and a faster timer competes with the UI it is trying to keep responsive.

**Drop silently.** Rejected, and this is the part that matters most. Dropped events understate
every spike figure the trace reports. A silently truncated trace produces a *better-looking*
result than the machine earned, which is the opposite of what a diagnostic tool should do
when it loses data.

## Consequences

- The producer stays lock-free and O(1) whether the queue is empty or saturated, using an
  interlocked depth counter. `ConcurrentQueue.Count` is deliberately not used — it snapshots
  across segments, which is not a cost to pay on every interrupt.
- Concurrent producers can momentarily overshoot the ceiling between the increment and the
  decrement. That is accepted: this is a backpressure limit, not an allocation guarantee.
- When anything is dropped the analysis leads with a warning that its figures are a lower
  bound, and the activity log records the count.
- Saturation is itself a finding — it means the machine was generating interrupts faster than
  a 150 ms timer could consume them.

## Verification

`tests/DpcIsrBackpressureTests.cs` exercises the same increment-check-decrement discipline at
a rate and concurrency the view model cannot reach in a test (it needs a WPF dispatcher and a
live kernel session): a 3× flood is capped rather than unbounded, nothing is dropped while the
consumer keeps up, tracked depth never drifts from real queue length under 8 concurrent
producers, and 500,000 produces against a saturated queue stay O(1).

Not verified: behaviour against a real ETW flood large enough to saturate the ceiling. The
measured rate on this machine fills roughly 1% of the queue over a 30-second trace, so
reaching saturation needs a machine under sustained heavy interrupt load.
