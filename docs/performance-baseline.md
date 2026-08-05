# Performance baseline

Measured against the installed Release build (`C:\Program Files\LatencyBench`) on the development
machine. Every figure below was recorded by running the application, not estimated.

**Machine:** Windows 11 (10.0.26200), 12 logical processors, self-contained x64 build.

These are a reference point for spotting regressions, not a specification. A different machine will
produce different absolute numbers; what should hold anywhere is the *shape* — startup under a couple
of seconds, idle CPU trending toward zero, and no unbounded growth in memory, handles or threads.

## Startup, launch to first window

| Run | Time |
|-----|------|
| 1 (cold) | 2.76 s |
| 2 | 0.80 s |
| 3 | 0.91 s |
| 4 | 1.30 s |
| 5 | 0.82 s |

**min 0.80 s · avg 1.32 s · max 2.76 s (n=5)**

The first run is slower because the OS file cache is cold. Warm launches sit under a second.
Steady state after startup: ~124–157 MB working set, ~610–660 handles, 26 threads.

## Idle, 90 seconds on the Dashboard

| t | Working set | Δ | Handles | Δ | Threads | CPU |
|---|---|---|---|---|---|---|
| 15 s | 145.0 MB | +3.0 | 628 | −6 | 14 | 1.25 % |
| 30 s | 146.6 MB | +4.6 | 628 | −6 | 14 | 1.30 % |
| 45 s | 146.6 MB | +4.6 | 621 | −13 | 10 | 0.87 % |
| 60 s | 146.6 MB | +4.6 | 623 | −11 | 10 | 0.65 % |
| 75 s | 146.6 MB | +4.6 | 623 | −11 | 10 | 0.52 % |
| 90 s | 146.6 MB | +4.6 | 623 | −11 | 10 | 0.43 % |

Working set is flat from t=30 onward. Handles and threads *decrease* as startup work retires. CPU
percentage is cumulative-average since t=0, so its steady decline means the app is doing essentially
nothing while idle — which is the point, given it exists to measure latency it must not itself cause.

## Leak check, 40 tab navigations (5 cycles × 8 tabs)

| Cycle | Working set | Handles | Threads |
|---|---|---|---|
| baseline | 147.7 MB | 650 | 12 |
| 1 | 172.7 MB | 710 | 30 |
| 2 | 177.5 MB | 711 | 30 |
| 3 | 173.6 MB | 711 | 30 |
| 4 | 171.4 MB | 711 | 30 |
| 5 | 174.9 MB | 696 | 20 |

The first cycle constructs each tab's view and starts its background work, which is the one-off cost
from baseline. After that everything plateaus: handles stop at 711 and then fall, threads likewise.
Working set oscillates within a few MB with no upward trend. No leak.

Monitor and DPC/ISR are excluded from this loop — both begin long-running captures, which would
measure the capture rather than the navigation.

## DPC/ISR trace session, four consecutive start/stop cycles

| Cycle | Samples | Stop + dispose |
|---|---|---|
| 1 | 3 571 | 2.25 s |
| 2 | 2 211 | 2.18 s |
| 3 | 1 120 | 2.18 s |
| 4 | 6 111 | 2.18 s |

Sample counts vary with what the machine was doing; every cycle collected real events, so each
session genuinely ran. Teardown is consistently ~2.2 s against the 15 s ceiling in
`DpcIsrTraceSession.ProcessingExitTimeout`, so that bound is not being approached in normal use — it
exists for a pathological case, not as a budget.

This matters because the NT Kernel Logger is a single system-wide session: a `Stop` that returned
before teardown finished would make the next `Start` fail, and the four consecutive cycles are what
prove it does not.

## How to reproduce

Startup, idle and leak figures come from driving the installed build through UI Automation from an
elevated PowerShell session — elevation is required because the app's manifest demands it, and
Windows UIPI blocks automating a higher-integrity window from an ordinary session.

The trace figures come from:

```
LATENCYBENCH_ETW=1 dotnet test -c Release --filter FullyQualifiedName~DpcIsrTraceSessionLiveTests
```

run elevated. That suite is opt-in for the same reason: the kernel session cannot be opened without
administrator rights.

## Not measured

- **Disk I/O and registry operation counts.** No instrumentation exists for either; adding it would
  mean shipping profiling hooks in release builds.
- **ETW overhead attributable to the app itself.** Measuring the observer's effect on what it
  observes needs a second, independent tracer; the sample counts above describe what was captured,
  not what capturing cost.
- **Behaviour on any machine other than this one**, and in particular on a low-core or low-memory
  system where the thread and handle plateaus would differ.
