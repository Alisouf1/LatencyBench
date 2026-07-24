namespace LatencyBench.Core.Interop;

internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
{
	public long IdleTime;

	public long KernelTime;

	public long UserTime;

	public long DpcTime;

	public long InterruptTime;

	public uint InterruptCount;
}
