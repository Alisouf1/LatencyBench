# LatencyBench

A Windows input-latency and interrupt diagnostic tool. It measures where latency actually
comes from on a specific machine — USB port jitter, DPC/ISR spikes, interrupt placement —
and only recommends a change when the measurement supports it.

**Version 1.1.0** · Windows 10 1809+ / Windows 11 · x64 · requires administrator

## What it does

| Tab | Purpose |
|---|---|
| **Dashboard** | Ranked action plan synthesised from every other tab's data |
| **Optimize** | Six profiles (Balanced, Gaming, Competitive FPS, Productivity, Streaming, Custom) that apply only what the analysis found, with rollback |
| **Port test** | Measures per-USB-port report jitter for a mouse or keyboard, ranks ports |
| **DPC/ISR** | ETW kernel trace of interrupt and deferred-procedure-call latency, attributed to the responsible driver |
| **MSI mode** | Message-signalled interrupt state per device |
| **Affinity** | Pins a device's interrupts to specific CPU cores |
| **Tweaks** | 12 individually reversible system tweaks, each with a recorded backup |
| **Mouse test** | Raw-input motion capture: path, speed bands, angle-snap detection |
| **Processes** | Per-process priority and I/O priority |
| **Monitor** | Live performance-counter sampling |

The recommendation engine runs 21 rules. Each returns one of three verdicts — actionable,
not applicable with a reason, or undetermined with the measurement it needs — so a check
that could not reach a conclusion is never reported as a clean result.

## Installing

Run `LatencyBench-Setup-1.1.0.exe`.

**Windows SmartScreen will warn you.** The installer is not code-signed, so click
**More info → Run anyway**. See [Limitations](#limitations).

Nothing else is needed — .NET is bundled. The app requires administrator rights at launch
to read ETW kernel traces and write device and power settings.

To remove it: **Settings → Apps → LatencyBench → Uninstall**. Your saved test history is
deliberately kept, not deleted.

### Distributing it

Ship the **installer**, or the whole publish folder. Do not hand someone only
`LatencyBench.App.exe` — DPC/ISR tracing will fail on their machine, because TraceEvent
loads its native dependencies from files that sit next to the executable. See
[ADR 001](docs/adr/001-no-single-file-publish.md).

## Building

```bash
dotnet build -c Release
dotnet test
dotnet format --verify-no-changes
```

Publishing and packaging:

```bash
dotnet publish src/LatencyBench.App/LatencyBench.App.csproj -c Release -o src/LatencyBench.App/bin/Release/net8.0-windows/win-x64/publish
ISCC.exe installer/LatencyBench.iss
```

Inno Setup 6 is required for the installer (`winget install --id JRSoftware.InnoSetup -e`).

The version lives in `Directory.Build.props`. `tools/verify-version-sync.ps1` fails the
build if it drifts from `installer/LatencyBench.iss` or `app.manifest`.

## Testing

`dotnet test` runs 845 tests and needs no special privileges.

Three suites are opt-in because they touch real system state or need elevation:

| Suite | Gate | What it does |
|---|---|---|
| `tools/test-installer.ps1` | elevated | 36 checks: install, upgrade, uninstall-while-running, rollback after a forced failure, registry cleanup, user data preservation |
| `DpcIsrTraceSessionLiveTests` | `LATENCYBENCH_ETW=1` + elevated | Opens a real ETW kernel session and asserts it collects samples |
| `EndToEndOptimizeVerificationTests` | `LATENCYBENCH_E2E=1` | Drives the full Optimize pipeline against the real registry, then restores every value it changed |

## Design notes

Architectural decisions are recorded in [`docs/adr/`](docs/adr/). Measured performance
figures are in [`docs/performance-baseline.md`](docs/performance-baseline.md).

Two principles run through the codebase and are worth knowing before changing it:

**A verdict is never inferred from a default value.** A struct's `default` is not a
measurement. Where data is absent the code says so rather than reporting zero — an absent
core is not a quiet core, and a check that could not run is not a check that passed.

**Applying a change is not the same as the change taking effect.** Every optimisation is
read back after writing; a value that does not stick is reported as a failure and rolled
back, not as success.

## Limitations

Stated plainly rather than left to be discovered:

- **Not code-signed.** SmartScreen warns on every fresh download until a certificate is
  obtained. The signing infrastructure is present but commented out in
  `installer/LatencyBench.iss`.
- **Not verified on a clean Windows install.** The build is self-contained and no hidden
  dependency was found, but that is not the same as a tested clean-machine install.
- **Standard-user and post-reboot behaviour are untested.** The app requires elevation to
  launch, so a standard user sees the UAC prompt, but that path has not been exercised
  end-to-end.
- **Disk I/O, registry operation counts and ETW self-overhead are unmeasured.** Measuring
  them would mean shipping profiling hooks in the release build.
- **Angle-snap scores recorded before v1.1.0** understate snapping on devices whose motion
  clustered just below a cardinal angle; the detection window was offset rather than
  centred. The scale is unchanged, so old and new figures remain comparable in kind.
