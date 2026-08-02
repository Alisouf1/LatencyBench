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

    /// <summary>
    /// The specific access right SetPriorityClass, NtSetInformationProcess and
    /// Process.ProcessorAffinity's setter all require. Checked on its own (rather than inferred from
    /// whether the process could be read) because anti-cheat drivers commonly strip exactly this
    /// right from handles opened by other processes to the game they protect while leaving query
    /// rights intact — the process is still fully readable, only writable state changes are refused.
    /// </summary>
    internal const uint ProcessSetInformation = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
