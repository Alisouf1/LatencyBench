using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LatencyBench.Core.Processes;

namespace LatencyBench.Tests;

/// <summary>
/// Tuning runs against the test host itself. Using the current process is deliberate: it is the one
/// process the test is allowed to modify, and every change is undone before the test returns.
/// </summary>
public class ProcessTunerTests : IDisposable
{
    private readonly ProcessTuner _tuner = new();
    private readonly ProcessPriorityClass _originalPriority;
    private readonly IntPtr _originalAffinity;

    public ProcessTunerTests()
    {
        using Process self = Process.GetCurrentProcess();
        _originalPriority = self.PriorityClass;
        _originalAffinity = self.ProcessorAffinity;
    }

    public void Dispose()
    {
        // Restored unconditionally. A test that left the runner pinned to one core would slow down
        // every test after it and look like flakiness.
        using Process self = Process.GetCurrentProcess();
        try
        {
            self.PriorityClass = _originalPriority;
            self.ProcessorAffinity = _originalAffinity;
        }
        catch (Exception)
        {
            // Nothing useful to do if restoring fails during teardown.
        }
    }

    private static int SelfId => Environment.ProcessId;

    // ---- Refusals ---------------------------------------------------------------------------

    [Fact]
    public void RealtimePriorityIsRefused()
    {
        // A realtime process outranks the kernel threads that service input and storage. If it does
        // not yield, the machine stops responding and the user cannot open Task Manager to stop it.
        var error = Assert.Throws<ArgumentException>(
            () => _tuner.SetPriority(SelfId, ProcessPriorityClass.RealTime));

        Assert.Contains("Realtime", error.Message);
    }

    [Fact]
    public void CriticalIoPriorityIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => _tuner.SetIoPriority(SelfId, IoPriority.Critical));

        Assert.Contains("memory manager", error.Message);
    }

    [Fact]
    public void AnUndefinedPriorityClassIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _tuner.SetPriority(SelfId, (ProcessPriorityClass)9999));
    }

    [Fact]
    public void AnUndefinedIoPriorityIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _tuner.SetIoPriority(SelfId, (IoPriority)99));
    }

    [Fact]
    public void AnEmptyAffinitySelectionIsRefused()
    {
        // The same failure as an empty interrupt affinity mask: a process restricted to no processor
        // cannot be scheduled at all.
        var error = Assert.Throws<ArgumentException>(
            () => _tuner.SetAffinity(SelfId, Array.Empty<int>()));

        Assert.Contains("at least one", error.Message);
    }

    [Fact]
    public void AProcessorThatDoesNotExistIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _tuner.SetAffinity(SelfId, new[] { Environment.ProcessorCount }));
    }

    [Fact]
    public void ANegativeProcessorIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _tuner.SetAffinity(SelfId, new[] { -1 }));
    }

    [Fact]
    public void AffinityIsRefusedOnAMultiGroupMachine()
    {
        var topology = TestProfiles.Topology(physicalCores: 64, threadsPerCore: 2, processorGroups: 2);

        Assert.Throws<NotSupportedException>(() => _tuner.SetAffinity(SelfId, new[] { 0 }, topology));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("audiodg")]
    [InlineData("dwm")]
    public void CoreWindowsProcessesAreProtected(string name)
    {
        Assert.True(ProcessTuner.IsProtected(name));
    }

    [Fact]
    public void ProtectionIsCaseInsensitive()
    {
        Assert.True(ProcessTuner.IsProtected("CSRSS"));
        Assert.True(ProcessTuner.IsProtected("LsAsS"));
    }

    [Fact]
    public void OrdinaryApplicationsAreNotProtected()
    {
        Assert.False(ProcessTuner.IsProtected("notepad"));
        Assert.False(ProcessTuner.IsProtected("LatencyBench.App"));
    }

    // ---- Behaviour --------------------------------------------------------------------------

    [Fact]
    public void SetsAndReadsBackThePriorityClass()
    {
        _tuner.SetPriority(SelfId, ProcessPriorityClass.AboveNormal);

        using Process self = Process.GetCurrentProcess();
        Assert.Equal(ProcessPriorityClass.AboveNormal, self.PriorityClass);
    }

    [Fact]
    public void SetsAndReadsBackTheIoPriority()
    {
        using Process self = Process.GetCurrentProcess();

        _tuner.SetIoPriority(SelfId, IoPriority.Low);
        Assert.Equal(IoPriority.Low, ProcessTuner.ReadIoPriority(self.Handle));

        _tuner.SetIoPriority(SelfId, IoPriority.Normal);
        Assert.Equal(IoPriority.Normal, ProcessTuner.ReadIoPriority(self.Handle));
    }

    [Fact]
    public void SetsAffinityToASingleProcessor()
    {
        _tuner.SetAffinity(SelfId, new[] { 0 });

        using Process self = Process.GetCurrentProcess();
        Assert.Equal(new IntPtr(1), self.ProcessorAffinity);
    }

    [Fact]
    public void SetsAffinityToSeveralProcessors()
    {
        int[] chosen = Enumerable.Range(0, Math.Min(3, Environment.ProcessorCount)).ToArray();

        _tuner.SetAffinity(SelfId, chosen);

        using Process self = Process.GetCurrentProcess();
        ulong expected = chosen.Aggregate(0UL, (mask, processor) => mask | (1UL << processor));
        Assert.Equal(expected, unchecked((ulong)self.ProcessorAffinity.ToInt64()));
    }

    [Fact]
    public void ClearAffinityRestoresEveryProcessor()
    {
        _tuner.SetAffinity(SelfId, new[] { 0 });

        _tuner.ClearAffinity(SelfId);

        using Process self = Process.GetCurrentProcess();
        ulong all = Environment.ProcessorCount >= 64
            ? ulong.MaxValue
            : (1UL << Environment.ProcessorCount) - 1;
        Assert.Equal(all, unchecked((ulong)self.ProcessorAffinity.ToInt64()));
    }

    [Fact]
    public void AnEmptySelectionChangesNothing()
    {
        using Process before = Process.GetCurrentProcess();
        IntPtr original = before.ProcessorAffinity;

        Assert.Throws<ArgumentException>(() => _tuner.SetAffinity(SelfId, Array.Empty<int>()));

        using Process after = Process.GetCurrentProcess();
        Assert.Equal(original, after.ProcessorAffinity);
    }

    // ---- Enumeration ------------------------------------------------------------------------

    [Fact]
    public void ListingNeverIncludesProtectedProcesses()
    {
        var candidates = _tuner.ListCandidates(windowedOnly: false);

        Assert.All(candidates, candidate => Assert.False(ProcessTuner.IsProtected(candidate.Name)));
    }

    [Fact]
    public void ListingFindsTheTestHostWhenNotFilteringToWindows()
    {
        var candidates = _tuner.ListCandidates(windowedOnly: false);

        Assert.Contains(candidates, candidate => candidate.Id == SelfId);
    }

    [Fact]
    public void WindowedFilterIsASubsetOfEverything()
    {
        var all = _tuner.ListCandidates(windowedOnly: false).Select(c => c.Id).ToHashSet();
        var windowed = _tuner.ListCandidates(windowedOnly: true);

        Assert.All(windowed, candidate => Assert.Contains(candidate.Id, all));
    }

    [Fact]
    public void EveryCandidateIsNamedAndUniquelyIdentified()
    {
        var candidates = _tuner.ListCandidates(windowedOnly: false);

        Assert.All(candidates, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate.Name)));
        Assert.Equal(candidates.Count, candidates.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void DisplayNameFallsBackToTheProcessNameWithoutAWindowTitle()
    {
        var withoutTitle = new TunableProcess(1, "game", null, null, null, null, true, true);
        var withTitle = new TunableProcess(2, "game", "My Game", null, null, null, true, true);

        Assert.Equal("game", withoutTitle.DisplayName);
        Assert.Equal("game — My Game", withTitle.DisplayName);
    }

    // ---- Write-access detection ---------------------------------------------------------------

    [Fact]
    public void CanModifyReturnsTrueForAProcessThisTestIsAllowedToChange()
    {
        // Proven by the SetPriority/SetIoPriority/SetAffinity tests elsewhere in this file actually
        // succeeding against the test host itself — this just checks the probe agrees up front.
        Assert.True(ProcessTuner.CanModify(SelfId));
    }

    [Fact]
    public void CanModifyReturnsFalseForTheSystemProcess()
    {
        // PID 4 ("System") is the kernel's own pseudo-process. It cannot be opened with
        // PROCESS_SET_INFORMATION by any normal-privilege caller — including an elevated one without
        // SeDebugPrivilege explicitly enabled — regardless of anti-cheat being involved at all. That
        // makes it a real, deterministic stand-in for "a process this app cannot get write access to",
        // without needing actual anti-cheat software to reproduce the scenario. Read-only: this never
        // attempts to change anything about PID 4, only whether a handle with that one access right
        // can be opened.
        Assert.False(ProcessTuner.CanModify(4));
    }

    [Fact]
    public void ListingReportsThatTheTestHostCanBeModified()
    {
        var candidates = _tuner.ListCandidates(windowedOnly: false);

        var self = Assert.Single(candidates, candidate => candidate.Id == SelfId);
        Assert.True(self.CanModify);
    }

    [Fact]
    public void EnumerationDoesNotThrowOnAMachineFullOfProtectedProcesses()
    {
        // Processes exit constantly during a full enumeration; the listing must tolerate that rather
        // than failing the whole call.
        for (int i = 0; i < 3; i++)
        {
            Assert.NotEmpty(_tuner.ListCandidates(windowedOnly: false));
        }
    }
}
