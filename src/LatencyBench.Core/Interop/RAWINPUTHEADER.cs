namespace LatencyBench.Core.Interop;

internal struct RAWINPUTHEADER
{
	public uint dwType;

	public uint dwSize;

	public nint hDevice;

	public nint wParam;
}
