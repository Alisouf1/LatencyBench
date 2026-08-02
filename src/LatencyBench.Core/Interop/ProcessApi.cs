using System;
using System.Runtime.InteropServices;

namespace LatencyBench.Core.Interop;

/// <summary>
/// Process I/O priority. There is no Win32 API for this at all — the scheduler exposes it only
/// through NtSetInformationProcess — which is why Task Manager can show a process's I/O priority
/// but offers no way to change it.
/// </summary>
internal static class ProcessApi
{
    /// <summary>PROCESSINFOCLASS.ProcessIoPriority.</summary>
    internal const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationProcess(
        nint processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength);

    /// <summary>Converts an NTSTATUS into the Win32 error the rest of the framework understands.</summary>
    [DllImport("ntdll.dll")]
    internal static extern int RtlNtStatusToDosError(int status);

    internal const int StatusSuccess = 0;
}
