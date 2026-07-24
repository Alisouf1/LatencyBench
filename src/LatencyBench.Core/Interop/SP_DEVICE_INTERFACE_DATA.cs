using System;

namespace LatencyBench.Core.Interop;

internal struct SP_DEVICE_INTERFACE_DATA
{
	public uint cbSize;

	public Guid InterfaceClassGuid;

	public uint Flags;

	public nint Reserved;
}
