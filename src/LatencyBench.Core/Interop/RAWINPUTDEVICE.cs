namespace LatencyBench.Core.Interop;

internal struct RAWINPUTDEVICE
{
    public ushort usUsagePage;

    public ushort usUsage;

    public uint dwFlags;

    public nint hwndTarget;
}
