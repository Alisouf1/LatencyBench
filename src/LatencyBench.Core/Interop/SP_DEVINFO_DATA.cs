using System;

namespace LatencyBench.Core.Interop;

internal struct SP_DEVINFO_DATA
{
	public uint cbSize;

	public Guid ClassGuid;

	public uint DevInst;

	public nint Reserved;
}
