using System;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using Microsoft.Win32;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises the real <see cref="RegistryDwordTweak"/> against a throwaway subkey under HKCU rather
/// than the HKLM paths the catalog actually uses, so Apply/GetState/Revert can be asserted without
/// touching any real system setting. HKCU is used only as a safe, writable sandbox — the class under
/// test is identical to what every HKLM-based catalog tweak (gpu.hardware-scheduling,
/// network.throttling-index, timer.global-resolution-requests, etc.) actually runs.
/// </summary>
public sealed class RegistryDwordTweakTests : IDisposable
{
    private const string ValueName = "TestValue";
    private const int AppliedValue = 1;
    private const int FallbackDefaultValue = 0;

    private readonly string _subKeyPath;
    private readonly string _backupFilePath;
    private readonly string _backupDirectory;

    public RegistryDwordTweakTests()
    {
        _subKeyPath = $@"Software\LatencyBenchTests\{Guid.NewGuid():N}";
        _backupDirectory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupDirectory);
        _backupFilePath = Path.Combine(_backupDirectory, "tweak-backups.json");
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_subKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // Leftover test keys under HKCU are harmless.
        }

        try
        {
            Directory.Delete(_backupDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private RegistryDwordTweak CreateTweak(TweakBackupStore? backupStore = null) =>
        new(
            new TweakDefinition
            {
                Id = "test.dword-tweak",
                Category = TweakCategory.Cpu,
                Name = "Test dword tweak",
                Description = "A throwaway tweak definition for RegistryDwordTweak tests.",
            },
            RegistryHive.CurrentUser,
            _subKeyPath,
            ValueName,
            AppliedValue,
            FallbackDefaultValue,
            backupStore ?? new TweakBackupStore(_backupFilePath));

    private int? ReadRawValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    private void WriteRawValue(int value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
        key.SetValue(ValueName, value, RegistryValueKind.DWord);
    }

    [Fact]
    public void GetStateReturnsUnknownWhenTheValueIsAbsent()
    {
        var tweak = CreateTweak();

        Assert.Equal(TweakState.Unknown, tweak.GetState());
    }

    [Fact]
    public void GetStateReturnsNotAppliedWhenTheCurrentValueDiffersFromTheAppliedValue()
    {
        WriteRawValue(FallbackDefaultValue);
        var tweak = CreateTweak();

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void GetStateReturnsAppliedWhenTheCurrentValueMatches()
    {
        WriteRawValue(AppliedValue);
        var tweak = CreateTweak();

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void ApplyWritesTheAppliedValue()
    {
        WriteRawValue(FallbackDefaultValue);
        var tweak = CreateTweak();

        tweak.Apply();

        Assert.Equal(AppliedValue, ReadRawValue());
        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void ApplyWhenTheValueIsAbsentCreatesItWithTheAppliedValue()
    {
        var tweak = CreateTweak();

        tweak.Apply();

        Assert.Equal(AppliedValue, ReadRawValue());
    }

    [Fact]
    public void RevertRestoresTheValueThatWasThereBeforeApply()
    {
        const int originalValue = 7;
        WriteRawValue(originalValue);
        var tweak = CreateTweak();
        tweak.Apply();

        tweak.Revert();

        Assert.Equal(originalValue, ReadRawValue());
    }

    [Fact]
    public void RevertDeletesTheValueIfItWasAbsentBeforeApply()
    {
        // Apply() on an absent value records the "absent" sentinel rather than a fabricated default,
        // so Revert() must remove the value entirely and leave the key exactly as it found it.
        var tweak = CreateTweak();
        tweak.Apply();
        Assert.NotNull(ReadRawValue());

        tweak.Revert();

        Assert.Null(ReadRawValue());
    }

    [Fact]
    public void RevertRemovesTheBackupEntrySoASecondRevertIsANoOp()
    {
        const int originalValue = 3;
        WriteRawValue(originalValue);
        var backupStore = new TweakBackupStore(_backupFilePath);
        var tweak = CreateTweak(backupStore);
        tweak.Apply();

        tweak.Revert();
        WriteRawValue(999); // Something Revert should not touch a second time.
        tweak.Revert();

        Assert.Equal(999, ReadRawValue());
    }

    [Fact]
    public void RevertWithNoPriorApplyIsANoOp()
    {
        WriteRawValue(AppliedValue);
        var tweak = CreateTweak();

        tweak.Revert();

        // Nothing was ever backed up (the value was already at AppliedValue), so there is no known
        // prior state to restore — Revert must leave the value exactly where it found it.
        Assert.Equal(AppliedValue, ReadRawValue());
    }

    [Fact]
    public void ApplyingTwiceDoesNotOverwriteTheOriginalBackupWithTheAppliedValue()
    {
        const int originalValue = 42;
        WriteRawValue(originalValue);
        var backupStore = new TweakBackupStore(_backupFilePath);
        var tweak = CreateTweak(backupStore);

        tweak.Apply(); // Backs up 42.
        tweak.Apply(); // Current value is now AppliedValue — must not clobber the 42 backup.
        tweak.Revert();

        Assert.Equal(originalValue, ReadRawValue());
    }
}
