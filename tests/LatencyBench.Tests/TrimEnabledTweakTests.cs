using System;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Storage;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises <see cref="TrimEnabledTweak"/> against a fake fsutil invocation rather than the real
/// tool, so Apply/Revert can be asserted without changing this machine's actual system-wide TRIM
/// setting.
/// <para>
/// The regression this suite exists for: <see cref="Revert"/> used to just call <see cref="Apply"/>
/// again, which force-enabled TRIM unconditionally instead of restoring whatever state the machine
/// was in before LatencyBench touched it. <see cref="RevertAfterApplyRestoresThePriorDisabledState"/>
/// fails against that old implementation and passes against the fixed one.
/// </para>
/// </summary>
public sealed class TrimEnabledTweakTests : IDisposable
{
    private readonly string _backupDirectory;
    private readonly string _backupFilePath;

    public TrimEnabledTweakTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupDirectory);
        _backupFilePath = Path.Combine(_backupDirectory, "tweak-backups.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_backupDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private TrimEnabledTweak CreateTweak(FakeFsutil fake) =>
        new(new TweakBackupStore(_backupFilePath), fake.Run);

    [Fact]
    public void GetStateReturnsAppliedWhenTrimIsEnabled()
    {
        var fake = new FakeFsutil { State = 0 };

        Assert.Equal(TweakState.Applied, CreateTweak(fake).GetState());
    }

    [Fact]
    public void GetStateReturnsNotAppliedWhenTrimIsDisabled()
    {
        var fake = new FakeFsutil { State = 1 };

        Assert.Equal(TweakState.NotApplied, CreateTweak(fake).GetState());
    }

    [Fact]
    public void GetStateReturnsUnknownWhenTheQueryFails()
    {
        var fake = new FakeFsutil { QueryExitCode = 1 };

        Assert.Equal(TweakState.Unknown, CreateTweak(fake).GetState());
    }

    [Fact]
    public void ApplyEnablesTrimWhenItWasDisabled()
    {
        var fake = new FakeFsutil { State = 1 };
        var tweak = CreateTweak(fake);

        tweak.Apply();

        Assert.Equal(0, fake.State);
        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void RevertAfterApplyRestoresThePriorDisabledState()
    {
        // This is the bug: Revert() used to call Apply() again, which force-enabled TRIM regardless
        // of what it was before — silently discarding a deliberate "TRIM disabled" choice.
        var fake = new FakeFsutil { State = 1 };
        var tweak = CreateTweak(fake);
        tweak.Apply();
        Assert.Equal(0, fake.State);

        tweak.Revert();

        Assert.Equal(1, fake.State);
        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void ApplyDoesNotRecordABackupWhenTrimWasAlreadyEnabled()
    {
        var fake = new FakeFsutil { State = 0 };
        var tweak = CreateTweak(fake);
        tweak.Apply();

        // Simulate something else disabling TRIM after Apply ran. Because Apply() never saved a
        // backup (there was nothing to restore), Revert() must leave this alone.
        fake.State = 1;
        tweak.Revert();

        Assert.Equal(1, fake.State);
    }

    [Fact]
    public void RevertRemovesTheBackupEntrySoASecondRevertIsANoOp()
    {
        var fake = new FakeFsutil { State = 1 };
        var tweak = CreateTweak(fake);
        tweak.Apply();

        tweak.Revert();
        fake.State = 0; // Something else changes it after the legitimate revert.
        tweak.Revert();

        Assert.Equal(0, fake.State);
    }

    [Fact]
    public void ApplyThrowsWhenFsutilFailsToSetTheValue()
    {
        var fake = new FakeFsutil { State = 1, SetExitCode = 1 };
        var tweak = CreateTweak(fake);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
    }

    [Fact]
    public void RevertThrowsWhenFsutilFailsToRestoreTheValue()
    {
        var fake = new FakeFsutil { State = 1 };
        var tweak = CreateTweak(fake);
        tweak.Apply();

        fake.SetExitCode = 1;

        Assert.Throws<InvalidOperationException>(() => tweak.Revert());
    }

    [Fact]
    public void ApplyThrowsWhenTheQueryOutputCannotBeParsed()
    {
        // Regression: silently proceeding here — as the old bare "= 0"/"= 1" substring check would
        // have, quietly returning null and letting Apply carry on with no backup — risks permanently
        // losing the ability to restore an actually-disabled prior state, the exact bug this class
        // was already fixed for once with Revert. Failing loudly here is the same fix applied to the
        // read step instead of just the revert step.
        var fake = new FakeFsutil { QueryOutputOverride = "Some unexpected output with no recognizable value" };
        var tweak = CreateTweak(fake);

        Assert.Throws<InvalidOperationException>(() => tweak.Apply());
    }

    [Fact]
    public void GetStateReturnsUnknownWhenTheQueryOutputCannotBeParsed()
    {
        var fake = new FakeFsutil { QueryOutputOverride = "garbage, no recognizable value here" };

        Assert.Equal(TweakState.Unknown, CreateTweak(fake).GetState());
    }

    [Fact]
    public void ReadsTheNtfsValueSpecificallyWhenReFSDisagrees()
    {
        // fsutil reports NTFS and ReFS on separate lines and they can legitimately differ. This tweak
        // is about the SSD/NTFS volume specifically, so it must read the NTFS line rather than
        // whichever "= 0"/"= 1" happens to appear first in the combined output.
        var fake = new FakeFsutil { QueryOutputOverride = "NTFS DisableDeleteNotify = 1\r\nReFS DisableDeleteNotify = 0\r\n" };

        Assert.Equal(TweakState.NotApplied, CreateTweak(fake).GetState());
    }

    [Fact]
    public void ParsingIgnoresLocalizedTextAroundTheNtfsValue()
    {
        // Stand-in for fsutil's localized surrounding text on non-English Windows: "NTFS" and
        // "DisableDeleteNotify" are technical identifiers fsutil does not translate, but descriptive
        // words around them can be — parsing must not depend on any of that other text.
        var fake = new FakeFsutil { QueryOutputOverride = "NTFS DisableDeleteNotify = 0 (activé / включено)" };

        Assert.Equal(TweakState.Applied, CreateTweak(fake).GetState());
    }

    /// <summary>Stands in for fsutil.exe: tracks a simulated DisableDeleteNotify value (0 = TRIM
    /// enabled, 1 = disabled) and answers "behavior query/set DisableDeleteNotify" the same way the
    /// real tool's stdout does — "NTFS DisableDeleteNotify = &lt;value&gt;" plus a matching ReFS line,
    /// unless a test supplies <see cref="QueryOutputOverride"/> for a specific output shape.</summary>
    private sealed class FakeFsutil
    {
        public int State { get; set; }

        public int QueryExitCode { get; set; }

        public int SetExitCode { get; set; }

        public string? QueryOutputOverride { get; set; }

        public (int ExitCode, string StdOut, string StdErr) Run(string fileName, string[] arguments)
        {
            Assert.Equal("fsutil.exe", fileName);

            if (arguments is ["behavior", "query", "DisableDeleteNotify"])
            {
                if (QueryExitCode != 0)
                {
                    return (QueryExitCode, string.Empty, "query failed");
                }

                string output = QueryOutputOverride ?? $"NTFS DisableDeleteNotify = {State}\r\nReFS DisableDeleteNotify = {State}\r\n";
                return (0, output, string.Empty);
            }

            if (arguments is ["behavior", "set", "DisableDeleteNotify", var rawValue])
            {
                if (SetExitCode != 0)
                {
                    return (SetExitCode, string.Empty, "set failed");
                }

                State = int.Parse(rawValue);
                return (0, string.Empty, string.Empty);
            }

            throw new InvalidOperationException("Unexpected fsutil invocation: " + string.Join(' ', arguments));
        }
    }
}
