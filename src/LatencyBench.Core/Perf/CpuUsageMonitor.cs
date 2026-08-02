using System;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Perf;

public sealed class CpuUsageMonitor
{
    /// <summary>
    /// The three arrays only ever mean anything together (same sample, same core count) — kept as one
    /// nullable snapshot instead of three independently-nullable fields so that invariant is enforced
    /// by the type system rather than by every caller remembering to check all three.
    /// </summary>
    private readonly record struct Snapshot(long[] Idle, long[] Kernel, long[] User);

    private Snapshot? _previous;

    public double[] SampleUsagePercent()
    {
        int processorCount = Environment.ProcessorCount;
        int num = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        nint num2 = Marshal.AllocHGlobal(num * processorCount);
        try
        {
            if (NtDllApi.NtQuerySystemInformation(8, num2, num * processorCount, out var _) != 0)
            {
                return new double[processorCount];
            }
            long[] array = new long[processorCount];
            long[] array2 = new long[processorCount];
            long[] array3 = new long[processorCount];
            for (int i = 0; i < processorCount; i++)
            {
                SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION sYSTEM_PROCESSOR_PERFORMANCE_INFORMATION = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(num2 + i * num);
                array[i] = sYSTEM_PROCESSOR_PERFORMANCE_INFORMATION.IdleTime;
                array2[i] = sYSTEM_PROCESSOR_PERFORMANCE_INFORMATION.KernelTime;
                array3[i] = sYSTEM_PROCESSOR_PERFORMANCE_INFORMATION.UserTime;
            }
            double[] array4 = new double[processorCount];
            if (_previous is { } previous && previous.Idle.Length == processorCount)
            {
                for (int j = 0; j < processorCount; j++)
                {
                    long num3 = array[j] - previous.Idle[j];
                    long num4 = array2[j] - previous.Kernel[j] + (array3[j] - previous.User[j]);
                    array4[j] = ((num4 > 0) ? Math.Clamp(100.0 * (double)(num4 - num3) / (double)num4, 0.0, 100.0) : 0.0);
                }
            }
            _previous = new Snapshot(array, array2, array3);
            return array4;
        }
        finally
        {
            Marshal.FreeHGlobal(num2);
        }
    }
}
