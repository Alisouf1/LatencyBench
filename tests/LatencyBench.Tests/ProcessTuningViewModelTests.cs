using System;
using System.Diagnostics;
using LatencyBench.App.ViewModels;
using LatencyBench.Core.Processes;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises <see cref="ProcessTuningViewModel"/>/<see cref="ProcessRowViewModel"/> against a fake
/// <see cref="ProcessTuner"/> rather than real processes, so the row-update and error-routing logic
/// can be asserted without touching anything real.
/// <para>
/// The regression this suite exists for: confirmed against a real EasyAntiCheat-protected process
/// (ARC Raiders) that the proactive <see cref="TunableProcess.CanModify"/> probe does not always
/// detect protection — EAC allows the OpenProcess(PROCESS_SET_INFORMATION) the probe performs to
/// succeed while still silently blocking the actual write. <see cref="ProcessWriteBlockedException"/>
/// is the write-then-verify layer's way of reporting that after the fact, and
/// <see cref="SetPriorityMarksTheRowConfirmedWriteBlockedWhenTheTunerReportsIt"/> proves the row
/// reacts to it by disabling itself instead of leaving the buttons clickable forever.
/// </para>
/// </summary>
public sealed class ProcessTuningViewModelTests
{
    private static ProcessRowViewModel CreateRow(ProcessTuningViewModel owner, bool canModify = true) =>
        new(
            new TunableProcess(1234, "game", "Game Window", ProcessPriorityClass.Normal, IoPriority.Normal, null, true, canModify),
            owner);

    [Fact]
    public void SetPriorityUpdatesTheRowOnSuccess()
    {
        var owner = new ProcessTuningViewModel(new FakeProcessTuner());
        var row = CreateRow(owner);

        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.Equal(ProcessPriorityClass.High, row.Priority);
        Assert.Null(row.RowError);
        Assert.True(row.CanModify);
        Assert.Equal($"{row.DisplayName}: priority set to High.", owner.Status);
    }

    [Fact]
    public void SetPriorityDoesNotUpdateTheRowWhenTheTunerThrows()
    {
        // The bug this guards against: the row used to update to "look" changed regardless of whether
        // the underlying write actually took effect.
        var tuner = new FakeProcessTuner { PriorityException = new InvalidOperationException("boom") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.Equal(ProcessPriorityClass.Normal, row.Priority);
        Assert.Equal("boom", row.RowError);
    }

    [Fact]
    public void APlainRefusalIsShownOnTheRowNotOnThePage()
    {
        var tuner = new FakeProcessTuner { PriorityException = new InvalidOperationException("boom") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.Equal("boom", row.RowError);
        Assert.Null(owner.Status);
    }

    [Fact]
    public void APlainRefusalDoesNotMarkTheRowWriteBlocked()
    {
        // Distinguishes an ordinary refusal (bad input, core-Windows-process guard, process exited)
        // from the anti-cheat-shaped ProcessWriteBlockedException — only the latter should disable the
        // row going forward.
        var tuner = new FakeProcessTuner { PriorityException = new InvalidOperationException("some other reason") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.True(row.CanModify);
        Assert.False(row.IsWriteProtected);
    }

    [Fact]
    public void SetPriorityMarksTheRowConfirmedWriteBlockedWhenTheTunerReportsIt()
    {
        var tuner = new FakeProcessTuner { PriorityException = new ProcessWriteBlockedException("silently refused") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner, canModify: true);

        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.False(row.CanModify);
        Assert.True(row.IsWriteProtected);
        Assert.Equal("silently refused", row.RowError);
        Assert.Contains("attempted and silently refused", row.DisabledReason);
    }

    [Fact]
    public void SetIoPriorityMarksTheRowConfirmedWriteBlockedWhenTheTunerReportsIt()
    {
        var tuner = new FakeProcessTuner { IoPriorityException = new ProcessWriteBlockedException("silently refused") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.SetIoPriority(row, IoPriority.High);

        Assert.False(row.CanModify);
        Assert.Equal(IoPriority.Normal, row.IoPriority);
    }

    [Fact]
    public void ClearAffinityMarksTheRowConfirmedWriteBlockedWhenTheTunerReportsIt()
    {
        var tuner = new FakeProcessTuner { ClearAffinityException = new ProcessWriteBlockedException("silently refused") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.ClearAffinity(row);

        Assert.False(row.CanModify);
    }

    [Fact]
    public void AProactivelyDetectedRowShowsTheUnconfirmedWording()
    {
        // Distinguishes "the probe guessed protected" from "a write was actually refused" — the
        // proactive probe alone should never claim the stronger, confirmed wording.
        var owner = new ProcessTuningViewModel(new FakeProcessTuner());
        var row = CreateRow(owner, canModify: false);

        Assert.True(row.IsWriteProtected);
        Assert.Contains("appears to be", row.DisabledReason);
        Assert.DoesNotContain("attempted and silently refused", row.DisabledReason);
    }

    [Fact]
    public void ARowRecoversFromConfirmedWriteBlockedAfterALaterSuccessfulWrite()
    {
        // Neither signal — the proactive probe nor an earlier confirmed refusal — is permanent. If a
        // later attempt on the same row actually succeeds, the row should stop looking protected
        // instead of leaving its buttons disabled forever based on a now-stale signal.
        var tuner = new FakeProcessTuner { PriorityException = new ProcessWriteBlockedException("silently refused") };
        var owner = new ProcessTuningViewModel(tuner);
        var row = CreateRow(owner);

        owner.SetPriority(row, ProcessPriorityClass.High);
        Assert.False(row.CanModify);

        tuner.PriorityException = null;
        owner.SetPriority(row, ProcessPriorityClass.High);

        Assert.True(row.CanModify);
        Assert.False(row.IsWriteProtected);
        Assert.Null(row.RowError);
    }

    /// <summary>A fake of the write-side of <see cref="ProcessTuner"/> that never touches a real
    /// process. Each Set*/Clear* method throws whatever is configured instead, or succeeds silently —
    /// enough to drive <see cref="ProcessTuningViewModel"/>'s error-routing logic without needing a
    /// real (let alone anti-cheat-protected) process to reproduce it against.</summary>
    private sealed class FakeProcessTuner : ProcessTuner
    {
        public Exception? PriorityException { get; set; }

        public Exception? IoPriorityException { get; set; }

        public Exception? ClearAffinityException { get; set; }

        public override void SetPriority(int processId, ProcessPriorityClass priority)
        {
            if (PriorityException is not null)
            {
                throw PriorityException;
            }
        }

        public override void SetIoPriority(int processId, IoPriority priority)
        {
            if (IoPriorityException is not null)
            {
                throw IoPriorityException;
            }
        }

        public override void ClearAffinity(int processId)
        {
            if (ClearAffinityException is not null)
            {
                throw ClearAffinityException;
            }
        }
    }
}
