using System;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Perf;

public class CpuUsageMonitor
{
    /// <summary>
    /// The three arrays only ever mean anything together (same sample, same core count) — kept as one
    /// nullable snapshot instead of three independently-nullable fields so that invariant is enforced
    /// by the type system rather than by every caller remembering to check all three.
    /// </summary>
    private readonly record struct Snapshot(long[] Idle, long[] Kernel, long[] User);

    private Snapshot? _previous;

    /// <summary>
    /// The raw kernel call, isolated behind a virtual seam so a test can simulate
    /// NtQuerySystemInformation returning a failing NTSTATUS without needing to actually break the
    /// syscall on the test host — which, called with a correctly sized buffer, essentially always
    /// succeeds.
    /// </summary>
    protected virtual int QuerySystemInformation(nint buffer, int size) =>
        NtDllApi.NtQuerySystemInformation(8, buffer, size, out _);

    /// <summary>
    /// Per-core CPU usage since the previous call, or null if it could not be determined this time.
    /// <para>
    /// Null covers two distinct cases a caller must not tell apart from genuine 0% usage: the kernel
    /// call itself failing, and there being no prior sample yet to compute a delta against (the very
    /// first call after construction, or the first call after a failed one — the stale snapshot is
    /// dropped on failure rather than kept, since diffing against it later would silently average
    /// usage over more than one sampling interval instead of showing anything was wrong). A genuine
    /// 0.0 in the returned array means "this core measurably did no work," never "unknown."
    /// </para>
    /// </summary>
    public double[]? SampleUsagePercent()
    {
        int processorCount = Environment.ProcessorCount;
        int structSize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        nint buffer = Marshal.AllocHGlobal(structSize * processorCount);
        try
        {
            if (QuerySystemInformation(buffer, structSize * processorCount) != 0)
            {
                _previous = null;
                return null;
            }

            long[] idle = new long[processorCount];
            long[] kernel = new long[processorCount];
            long[] user = new long[processorCount];
            for (int i = 0; i < processorCount; i++)
            {
                SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION info =
                    Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * structSize);
                idle[i] = info.IdleTime;
                kernel[i] = info.KernelTime;
                user[i] = info.UserTime;
            }

            var current = new Snapshot(idle, kernel, user);

            if (_previous is not { } previous || previous.Idle.Length != processorCount)
            {
                // No usable baseline — the very first call, the first call after a failure, or the
                // processor count changed since the last one. Returning zeros here would be
                // indistinguishable from every core genuinely being idle; null says plainly that this
                // sample isn't a measurement at all.
                _previous = current;
                return null;
            }

            double[] usage = new double[processorCount];
            for (int j = 0; j < processorCount; j++)
            {
                long idleDelta = idle[j] - previous.Idle[j];
                long totalDelta = kernel[j] - previous.Kernel[j] + (user[j] - previous.User[j]);
                usage[j] = totalDelta > 0 ? Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0.0, 100.0) : 0.0;
            }

            _previous = current;
            return usage;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
