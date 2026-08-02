using System.Runtime.InteropServices;

namespace LatencyBench.Core.Interop;

/// <summary>
/// Kernel timer resolution. There is no documented Win32 equivalent — timeGetDevCaps reports what
/// the multimedia timer supports, not what the system clock is currently running at — so these
/// ntdll exports are the only way to read the value the whole machine is actually using.
/// </summary>
internal static class TimerApi
{
    /// <summary>
    /// All three values come back in 100-nanosecond units. A "1 ms timer" is 10000.
    /// <para>
    /// Note the naming is the opposite of what it looks like: MinimumResolution is the coarsest
    /// interval (the largest number, normally 156250 = 15.625 ms) and MaximumResolution is the
    /// finest (the smallest number, normally 5000 = 0.5 ms).
    /// </para>
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryTimerResolution(
        out uint minimumResolution,
        out uint maximumResolution,
        out uint currentResolution);
}
