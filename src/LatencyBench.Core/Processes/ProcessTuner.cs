using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.Interop;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Core.Processes;

/// <summary>The scheduler's I/O priority hint for a process.</summary>
public enum IoPriority
{
    VeryLow = 0,
    Low = 1,
    Normal = 2,
    High = 3,

    /// <summary>Reserved for the memory manager. Never settable by an application.</summary>
    Critical = 4
}

/// <summary>A running process and the scheduling state that can be tuned.</summary>
/// <param name="CanModify">
/// Whether this process can plausibly have its priority, I/O priority or affinity changed at all —
/// checked by actually attempting to open it with the specific access right those operations need,
/// not inferred by name. Always false when <paramref name="IsAccessible"/> is false. Anti-cheat
/// drivers (EasyAntiCheat, BattlEye, Vanguard and similar) commonly strip write access to the game
/// they protect while leaving read access intact, which is exactly the case this distinguishes from
/// general inaccessibility.
/// </param>
public sealed record TunableProcess(
    int Id,
    string Name,
    string? MainWindowTitle,
    ProcessPriorityClass? PriorityClass,
    IoPriority? IoPriority,
    ulong? AffinityMask,
    bool IsAccessible,
    bool CanModify)
{
    public string DisplayName => string.IsNullOrWhiteSpace(MainWindowTitle)
        ? Name
        : $"{Name} — {MainWindowTitle}";
}

/// <summary>
/// Thrown when a write to a process's priority, I/O priority or affinity did not report an error but
/// the value did not actually change on re-read afterward — the anti-cheat-shaped failure mode.
/// <para>
/// A plain <see cref="InvalidOperationException"/> also covers unrelated refusals ("this is a core
/// Windows process", the process having exited between calls); this narrower type exists so a caller
/// can specifically recognize "the write was silently refused" and treat it as confirmation the
/// process is write-protected — including cases where <see cref="ProcessTuner.CanModify"/>'s
/// proactive access-right probe did not catch it in advance, because some anti-cheat drivers allow
/// the handle to be opened with the right access but still block the actual write underneath it.
/// </para>
/// </summary>
public sealed class ProcessWriteBlockedException : InvalidOperationException
{
    public ProcessWriteBlockedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads and changes per-process scheduling state.
/// <para>
/// Everything here is deliberately session-scoped. Priority class, I/O priority and processor
/// affinity live in the process object and die with it — there is no registry key that persists
/// them, and nothing in this class pretends otherwise. Anything claiming to make a game's priority
/// "permanent" is either relaunching it or using Image File Execution Options, which applies to
/// every launch of that executable and is a materially different and riskier thing.
/// </para>
/// </summary>
public class ProcessTuner
{
    /// <summary>
    /// Processes that must never be retuned. Starving any of these produces a machine that appears
    /// frozen, and the user has no obvious way to connect that to something they clicked here.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression",
        "csrss", "wininit", "winlogon", "services", "lsass", "smss",
        "dwm", "svchost", "ntoskrnl", "MsMpEng", "audiodg"
    };

    /// <summary>
    /// Lists processes worth offering. Filters to those with a visible window: the point is to raise
    /// the priority of the thing the user is actually running, and presenting all several hundred
    /// processes makes finding it harder, not easier.
    /// </summary>
    public IReadOnlyList<TunableProcess> ListCandidates(bool windowedOnly = true)
    {
        var results = new List<TunableProcess>();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsProtected(process.ProcessName))
                    {
                        continue;
                    }

                    string? title = SafeMainWindowTitle(process);
                    if (windowedOnly && string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    results.Add(Describe(process, title));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process exited between enumeration and inspection, or is protected. Neither
                    // is worth failing the whole listing over.
                }
            }
        }

        return results
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id)
            .ToList();
    }

    private static TunableProcess Describe(Process process, string? title)
    {
        ProcessPriorityClass? priority = null;
        ulong? affinity = null;
        IoPriority? ioPriority = null;
        bool accessible = true;

        try
        {
            priority = process.PriorityClass;
            affinity = unchecked((ulong)process.ProcessorAffinity.ToInt64());
            ioPriority = ReadIoPriority(process.Handle);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // A process running at a higher integrity level than this one cannot be inspected. It is
            // still listed, marked inaccessible, so the user sees why it cannot be tuned instead of
            // wondering why it is missing.
            accessible = false;
        }

        // No point probing write access on a process that could not even be read.
        bool canModify = accessible && CanModify(process.Id);

        return new TunableProcess(process.Id, process.ProcessName, title, priority, ioPriority, affinity, accessible, canModify);
    }

    /// <summary>
    /// Whether SetPriority/SetIoPriority/SetAffinity have any realistic chance of succeeding against
    /// this process, checked by actually attempting to open it with exactly the access right those
    /// calls need — not by name or by guessing from whether it is readable. This is the signal that
    /// distinguishes an anti-cheat-protected game (readable, but writes are refused) from a process
    /// that cannot be inspected at all.
    /// </summary>
    public static bool CanModify(int processId)
    {
        nint handle = ProcessApi.OpenProcess(ProcessApi.ProcessSetInformation, false, processId);
        if (handle == 0)
        {
            return false;
        }

        ProcessApi.CloseHandle(handle);
        return true;
    }

    private static string? SafeMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static bool IsProtected(string processName) => ProtectedProcessNames.Contains(processName);

    /// <summary>
    /// Sets the priority class.
    /// <para>
    /// Realtime is refused outright. A realtime-priority thread outranks every kernel thread that is
    /// not a hardware interrupt handler, including the ones that service the mouse, the keyboard and
    /// the disk. A busy loop at realtime does not make a game faster — it makes the machine
    /// unresponsive until that process yields, and the user cannot open Task Manager to stop it.
    /// </para>
    /// </summary>
    public virtual void SetPriority(int processId, ProcessPriorityClass priority)
    {
        if (priority == ProcessPriorityClass.RealTime)
        {
            throw new ArgumentException(
                "Realtime priority is not offered. It outranks the kernel threads that service input " +
                "and storage, so a process that does not yield leaves the machine unresponsive with no " +
                "way to intervene. High is the highest setting with a real benefit and no such failure mode.",
                nameof(priority));
        }

        if (!Enum.IsDefined(typeof(ProcessPriorityClass), priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown priority class.");
        }

        using Process process = Process.GetProcessById(processId);
        if (IsProtected(process.ProcessName))
        {
            throw new InvalidOperationException(
                $"'{process.ProcessName}' is a core Windows process and is not safe to retune.");
        }

        process.PriorityClass = priority;

        // Some anti-cheat drivers let SetPriorityClass return success without the change actually
        // taking effect, rather than failing the call outright. Re-reading after the write — the same
        // pattern PowerCfgAcValueTweak.WriteValueAndVerify uses — catches that instead of trusting the
        // call's return value, so the caller never believes a change happened when it did not.
        process.Refresh();
        if (process.PriorityClass != priority)
        {
            throw new ProcessWriteBlockedException(
                $"'{process.ProcessName}' did not actually change priority to {priority} (it is still " +
                $"{process.PriorityClass}), even though the request did not report an error. This usually " +
                "means anti-cheat or another security tool is protecting the process.");
        }
    }

    /// <summary>
    /// Sets the I/O priority hint.
    /// <para>
    /// Critical is refused: it is reserved for the memory manager's page writes, and the kernel will
    /// reject it anyway, so offering it would only produce a confusing failure.
    /// </para>
    /// </summary>
    public virtual void SetIoPriority(int processId, IoPriority priority)
    {
        if (priority == IoPriority.Critical)
        {
            throw new ArgumentException(
                "Critical I/O priority is reserved for the memory manager and cannot be set by an application.",
                nameof(priority));
        }

        if (!Enum.IsDefined(typeof(IoPriority), priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown I/O priority.");
        }

        using Process process = Process.GetProcessById(processId);
        if (IsProtected(process.ProcessName))
        {
            throw new InvalidOperationException(
                $"'{process.ProcessName}' is a core Windows process and is not safe to retune.");
        }

        int value = (int)priority;
        int status = ProcessApi.NtSetInformationProcess(
            process.Handle, ProcessApi.ProcessIoPriority, ref value, sizeof(int));

        if (status != ProcessApi.StatusSuccess)
        {
            throw new Win32Exception(
                ProcessApi.RtlNtStatusToDosError(status),
                $"Could not set the I/O priority of '{process.ProcessName}'.");
        }

        // See the comment in SetPriority: a success status does not guarantee the value actually took
        // effect. Re-read and confirm rather than trust the return code alone.
        IoPriority? actual = ReadIoPriority(process.Handle);
        if (actual != priority)
        {
            throw new ProcessWriteBlockedException(
                $"'{process.ProcessName}' did not actually change I/O priority to {priority} (it is still " +
                $"{(actual?.ToString() ?? "unreadable")}), even though the request did not report an error. " +
                "This usually means anti-cheat or another security tool is protecting the process.");
        }
    }

    public static IoPriority? ReadIoPriority(nint processHandle)
    {
        int value = 0;
        int status = ProcessApi.NtQueryInformationProcess(
            processHandle, ProcessApi.ProcessIoPriority, ref value, sizeof(int), out _);

        if (status != ProcessApi.StatusSuccess || !Enum.IsDefined(typeof(IoPriority), value))
        {
            return null;
        }

        return (IoPriority)value;
    }

    /// <summary>
    /// Pins a process to specific logical processors.
    /// <para>
    /// Validated the same way interrupt affinity is, and for the same reason: an empty mask or a
    /// processor that does not exist produces a configuration Windows will reject or, worse, accept
    /// into a state where the process cannot be scheduled at all.
    /// </para>
    /// </summary>
    public virtual void SetAffinity(int processId, IReadOnlyList<int> logicalProcessors, CpuTopology? topology = null)
    {
        if (logicalProcessors.Count == 0)
        {
            throw new ArgumentException(
                "A process needs at least one logical processor. To remove a restriction, set it back to all processors.",
                nameof(logicalProcessors));
        }

        int processorCount = Environment.ProcessorCount;
        foreach (int processor in logicalProcessors)
        {
            if (processor < 0 || processor >= processorCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalProcessors),
                    processor,
                    $"This PC has {processorCount} logical processors, so processor {processor} does not exist.");
            }
        }

        if (topology?.UsesMultipleProcessorGroups == true)
        {
            // Process.ProcessorAffinity carries a bare mask with no group number, exactly like the
            // interrupt affinity registry value, so it cannot address a specific group either.
            throw new NotSupportedException(
                $"This PC spreads {topology.LogicalProcessorCount} logical processors across " +
                $"{topology.ActiveProcessorGroupCount} processor groups. Process affinity here can only " +
                "address a single group, so it is not offered.");
        }

        using Process process = Process.GetProcessById(processId);
        if (IsProtected(process.ProcessName))
        {
            throw new InvalidOperationException(
                $"'{process.ProcessName}' is a core Windows process and is not safe to retune.");
        }

        ulong expectedMask = AffinityMask.FromCoreIndices(logicalProcessors);
        process.ProcessorAffinity = new IntPtr(unchecked((long)expectedMask));

        // See the comment in SetPriority: a write that does not report an error is not proof it took
        // effect. ProcessorAffinity's setter goes through the same PROCESS_SET_INFORMATION-gated path
        // anti-cheat drivers commonly intercept, so it gets the same re-read-and-confirm treatment.
        process.Refresh();
        if (unchecked((ulong)process.ProcessorAffinity.ToInt64()) != expectedMask)
        {
            throw new ProcessWriteBlockedException(
                $"'{process.ProcessName}' did not actually change its processor affinity, even though the " +
                "request did not report an error. This usually means anti-cheat or another security tool " +
                "is protecting the process.");
        }
    }

    /// <summary>Restores a process to every logical processor.</summary>
    public virtual void ClearAffinity(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        if (IsProtected(process.ProcessName))
        {
            throw new InvalidOperationException(
                $"'{process.ProcessName}' is a core Windows process and is not safe to retune.");
        }

        ulong all = Environment.ProcessorCount >= 64
            ? ulong.MaxValue
            : (1UL << Environment.ProcessorCount) - 1;

        process.ProcessorAffinity = new IntPtr(unchecked((long)all));

        process.Refresh();
        if (unchecked((ulong)process.ProcessorAffinity.ToInt64()) != all)
        {
            throw new ProcessWriteBlockedException(
                $"'{process.ProcessName}' did not actually restore its processor affinity, even though the " +
                "request did not report an error. This usually means anti-cheat or another security tool " +
                "is protecting the process.");
        }
    }
}
