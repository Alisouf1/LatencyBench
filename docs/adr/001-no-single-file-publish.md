# ADR 001 — Do not publish as a single file

**Status:** Accepted
**Date:** 2026-08-07

## Context

LatencyBench is published self-contained so the target machine needs no .NET install.
`PublishSingleFile` was additionally enabled to collapse the ~270-file output into one
executable, purely for packaging tidiness.

That broke DPC/ISR tracing outright — the single most important feature in the app.

The failure is not obvious from the symptom. TraceEvent loads `KernelTraceControl.dll`
by building a path from its own assembly's location. Under single-file publishing,
`Assembly.Location` is defined to return an empty string, so the lookup resolves to a
nonexistent root path and every trace attempt dies with:

```
System.ComponentModel.Win32Exception (126): The specified module could not be found.
   at Microsoft.Diagnostics.Tracing.ETWKernelControl.LoadKernelTraceControl()
```

The DLL was never missing. With `IncludeNativeLibrariesForSelfExtract` it was extracted
to the temp cache correctly — verified present at
`%TEMP%\.net\LatencyBench.App\<hash>\amd64\KernelTraceControl.dll`. TraceEvent simply had
no way to be told where the extraction went.

Nothing about this is visible at build time. It compiles with zero warnings, the app
starts, every other tab works, and only tracing fails.

## Decision

`PublishSingleFile` is **off**, and must stay off.

`SelfContained` and `RuntimeIdentifier=win-x64` remain, so the runtime is still bundled and
the target machine still needs nothing preinstalled. The native dependencies
(`amd64/KernelTraceControl.dll`, `amd64/msdia140.dll`, and the x86/arm64 equivalents) ship
as real files beside the executable, which is exactly what TraceEvent's loader expects.

## Alternatives considered

**Set `AppContext.BaseDirectory` or pre-load the DLL by absolute path before TraceEvent
runs.** Rejected: it depends on TraceEvent's internal resolution order, which is not part of
its public contract and could change in any release. The failure mode if it ever changed is
the same silent one — a trace that produces nothing.

**Copy the native DLLs beside the single-file exe as loose files.** Rejected: this produces
the worst of both, an exe that still is not self-contained plus a folder of DLLs, while
leaving the resolution dependent on `Assembly.Location` being non-empty, which it is not.

**Drop TraceEvent and call the ETW APIs directly.** Rejected as disproportionate. TraceEvent
is the supported, documented path for kernel-session tracing, and reimplementing its
provider and rundown handling to gain single-file packaging would trade a real feature's
reliability for a cosmetic one.

## Consequences

- The install folder contains ~270 files rather than one. The installer already packages
  the publish directory recursively, so nothing about installation or uninstallation changes.
- Distributing the app as a bare `.exe` is not possible; it must be the installer or the
  whole folder. Handing someone only `LatencyBench.App.exe` will break tracing on their
  machine in exactly the way described above.
- `tools/test-installer.ps1` asserts the native DLLs are present on disk. That assertion
  previously said the opposite — that the output was exactly one file — so it was itself
  encoding the bug.

## Verification

Measured both ways on real hardware:

| Build | Result |
|---|---|
| Single-file | Win32Exception 126 on every trace attempt |
| Multi-file | ETW kernel session starts; 21,329 samples collected in 5 s |

Covered by `tests/DpcIsrTraceSessionLiveTests.cs` (opt-in, requires elevation) and by the
`amd64/KernelTraceControl.dll` assertion in `tools/test-installer.ps1`.
