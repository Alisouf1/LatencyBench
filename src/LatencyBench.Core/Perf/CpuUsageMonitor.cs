using System;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Perf;

public sealed class CpuUsageMonitor
{
	private long[]? _prevIdle;

	private long[]? _prevKernel;

	private long[]? _prevUser;

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
			if (_prevIdle != null && _prevIdle.Length == processorCount)
			{
				for (int j = 0; j < processorCount; j++)
				{
					long num3 = array[j] - _prevIdle[j];
					long num4 = array2[j] - _prevKernel[j] + (array3[j] - _prevUser[j]);
					array4[j] = ((num4 > 0) ? Math.Clamp(100.0 * (double)(num4 - num3) / (double)num4, 0.0, 100.0) : 0.0);
				}
			}
			_prevIdle = array;
			_prevKernel = array2;
			_prevUser = array3;
			return array4;
		}
		finally
		{
			Marshal.FreeHGlobal(num2);
		}
	}
}
