# LatencyBench

Windows tool for measuring and tuning PC input/interrupt latency — built for gamers and power users who want proof, not guesses.

Every recommendation in the app is built on the same loop: **measure → change → measure again** to confirm it actually helped.

## Features

### Core Affinity
Pin interrupt handling for USB controllers, GPU, storage controllers, and audio devices to specific CPU cores. Includes a one-click **Optimize all** that assigns each device its own core — using real interrupt-load data from saved DPC/ISR traces when available, and deterministic (same input always gives the same result, no random reshuffling on every click).

### MSI Mode
Switch interrupt-capable devices to Message-Signaled Interrupts and set IRQ priority, with a rule-based advisor for which devices actually benefit.

### DPC/ISR Tracing
Traces kernel-level interrupt latency via ETW and identifies which driver is actually causing spikes — not just "high latency," but the specific `.sys` file responsible, with a plain-language suggested fix. Saved traces are compared against each other so a configuration change can be measured, not assumed.

### Port Test
Measures real jitter, polling rate, and report latency for a mouse or keyboard on a specific physical USB port — useful for finding the best port on a multi-controller motherboard.

### Mouse Test
Analyzes raw mouse motion: CPI measurement against a real physical distance, angle-snapping detection, speed-dependent gain bands, and side-by-side comparison of two saved runs.

### Tweaks
A curated set of real, reversible Windows/power/network tweaks (not registry-hack folklore) — every tweak has verified apply/revert with an automatic backup of the previous value.

### Dashboard
Synthesizes signals from every tab above into one ranked action plan, so you don't have to visit each tab separately to see what's actually worth fixing.

## Requirements

- Windows 10 or 11, 64-bit
- Administrator rights (interrupt affinity and MSI settings are written to the registry)

## Building

```
dotnet build LatencyBench.sln
```

Requires .NET 8 SDK. Open `LatencyBench.sln` in Visual Studio 2022, or build from the command line.

To produce a self-contained portable build (no .NET install required on the target machine):

```
dotnet publish src/LatencyBench.App/LatencyBench.App.csproj -c Release -r win-x64 --self-contained true -o <output-dir>
```

## License

by Alisouf
