using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Monitoring;

/// <summary>
/// Reads system-wide interrupt, DPC and processor time from the standard "Processor Information"
/// performance counters, and available memory from the same kernel call the memory info reader
/// uses.
/// <para>
/// This is deliberately not the DPC/ISR tab's approach. That tab runs a full ETW kernel trace,
/// which is the only way to name the driver responsible for a spike, but it monopolises the single
/// system-wide NT Kernel Logger session and costs noticeably more to keep running. Continuous
/// background monitoring needs the opposite trade: cheap enough to run for hours, at the cost of
/// only ever reporting an aggregate percentage rather than a driver name. The two are complementary
/// — this tab says "something is wrong, right now", the DPC/ISR tab says "here is what".
/// </para>
/// </summary>
public sealed class PerformanceCounterSource : ICounterSource
{
	private readonly PerformanceCounter _interruptTime =
		new("Processor Information", "% Interrupt Time", "_Total", readOnly: true);

	private readonly PerformanceCounter _dpcTime =
		new("Processor Information", "% DPC Time", "_Total", readOnly: true);

	private readonly PerformanceCounter _processorTime =
		new("Processor Information", "% Processor Time", "_Total", readOnly: true);

	public void Prime()
	{
		_interruptTime.NextValue();
		_dpcTime.NextValue();
		_processorTime.NextValue();
	}

	public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
	{
		return (_interruptTime.NextValue(), _dpcTime.NextValue(), _processorTime.NextValue());
	}

	public double SampleAvailableMemoryPercent()
	{
		var status = new SystemInfoApi.MEMORYSTATUSEX
		{
			dwLength = (uint)Marshal.SizeOf<SystemInfoApi.MEMORYSTATUSEX>()
		};

		if (!SystemInfoApi.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
		{
			throw new InvalidOperationException("Could not read available memory.");
		}

		// ullAvailPhys already accounts for reclaimable standby cache — it is the amount that can be
		// used immediately without writing anything to disk first — so this is the same figure Task
		// Manager's "Available" shows, not a naive free-list count that would false-alarm on a
		// machine using RAM for cache the way Windows is designed to.
		return 100.0 * status.ullAvailPhys / status.ullTotalPhys;
	}

	public void Dispose()
	{
		_interruptTime.Dispose();
		_dpcTime.Dispose();
		_processorTime.Dispose();
	}
}
