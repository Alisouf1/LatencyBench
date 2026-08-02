using System;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using Microsoft.Win32;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises the real <see cref="RegistryStringTweak"/> against a throwaway subkey under HKCU rather
/// than the HKLM MMCSS paths the catalog actually uses (mmcss.games-scheduling-category,
/// mmcss.games-sfio-priority), so Apply/GetState/Revert can be asserted without touching any real
/// system setting.
/// </summary>
public sealed class RegistryStringTweakTests : IDisposable
{
    private const string ValueName = "TestValue";
    private const string AppliedValue = "High";
    private const string FallbackDefaultValue = "Medium";

    private readonly string _subKeyPath;
    private readonly string _backupFilePath;
    private readonly string _backupDirectory;

    public RegistryStringTweakTests()
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

    private RegistryStringTweak CreateTweak(string appliedValue = AppliedValue, string fallbackDefaultValue = FallbackDefaultValue, TweakBackupStore? backupStore = null) =>
        new(
            new TweakDefinition
            {
                Id = "test.string-tweak",
                Category = TweakCategory.Cpu,
                Name = "Test string tweak",
                Description = "A throwaway tweak definition for RegistryStringTweak tests.",
            },
            RegistryHive.CurrentUser,
            _subKeyPath,
            ValueName,
            appliedValue,
            fallbackDefaultValue,
            backupStore ?? new TweakBackupStore(_backupFilePath));

    private string? ReadRawValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_subKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    private void WriteRawValue(string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    [Fact]
    public void GetStateReturnsNotAppliedWhenAbsentAndTheFallbackDefaultDiffersFromTheAppliedValue()
    {
        // Windows treats an absent value as "using its own default" — a known state, not Unknown —
        // so when that default differs from what LatencyBench would apply, it must read as NotApplied.
        var tweak = CreateTweak();

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void GetStateReturnsAppliedWhenAbsentAndTheFallbackDefaultMatchesTheAppliedValue()
    {
        var tweak = CreateTweak(appliedValue: "High", fallbackDefaultValue: "High");

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void GetStateReturnsAppliedWhenTheCurrentValueMatches()
    {
        WriteRawValue(AppliedValue);
        var tweak = CreateTweak();

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void GetStateComparisonIsCaseInsensitive()
    {
        WriteRawValue("high");
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
    }

    [Fact]
    public void RevertRestoresTheValueThatWasThereBeforeApply()
    {
        WriteRawValue(FallbackDefaultValue);
        var tweak = CreateTweak();
        tweak.Apply();

        tweak.Revert();

        Assert.Equal(FallbackDefaultValue, ReadRawValue());
    }

    [Fact]
    public void RevertDeletesTheValueIfItWasAbsentBeforeApply()
    {
        // Apply() on an absent value records a distinct "absent" sentinel rather than the fallback
        // default, so Revert() must remove the value entirely rather than writing the default back.
        var tweak = CreateTweak();
        tweak.Apply();
        Assert.NotNull(ReadRawValue());

        tweak.Revert();

        Assert.Null(ReadRawValue());
    }

    [Fact]
    public void RevertRemovesTheBackupEntrySoASecondRevertIsANoOp()
    {
        WriteRawValue(FallbackDefaultValue);
        var backupStore = new TweakBackupStore(_backupFilePath);
        var tweak = CreateTweak(backupStore: backupStore);
        tweak.Apply();

        tweak.Revert();
        WriteRawValue("SomethingElse"); // Something a second Revert should not touch.
        tweak.Revert();

        Assert.Equal("SomethingElse", ReadRawValue());
    }
}
