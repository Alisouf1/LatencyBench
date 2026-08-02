using System.Runtime.InteropServices;

namespace LatencyBench.Core.Interop;

internal static class NtDllApi
{
    public const int SystemProcessorPerformanceInformation = 8;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);
}
