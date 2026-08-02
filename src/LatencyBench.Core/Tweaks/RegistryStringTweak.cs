using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// A REG_SZ setting with a recorded original value.
/// <para>
/// The multimedia class scheduler's task profiles store several of their settings as strings rather
/// than DWORDs ("Scheduling Category" = "High", "SFIO Priority" = "High"), so
/// <see cref="RegistryDwordTweak"/> cannot express them.
/// </para>
/// </summary>
public sealed class RegistryStringTweak : ITweak
{
    private readonly RegistryHive _hive;
    private readonly string _subKeyPath;
    private readonly string _valueName;
    private readonly string _appliedValue;
    private readonly string _fallbackDefaultValue;
    private readonly TweakBackupStore _backupStore;

    public TweakDefinition Definition { get; }

    private string BackupKey => $"registry-sz:{_hive}\\{_subKeyPath}\\{_valueName}";

    public RegistryStringTweak(
        TweakDefinition definition,
        RegistryHive hive,
        string subKeyPath,
        string valueName,
        string appliedValue,
        string fallbackDefaultValue,
        TweakBackupStore backupStore)
    {
        Definition = definition;
        _hive = hive;
        _subKeyPath = subKeyPath;
        _valueName = valueName;
        _appliedValue = appliedValue;
        _fallbackDefaultValue = fallbackDefaultValue;
        _backupStore = backupStore;
    }

    public TweakState GetState()
    {
        string? current = ReadCurrentValue();
        if (current is null)
        {
            // An absent value means Windows is using its own default. That is a known state, not an
            // unknown one, so it is reported as NotApplied rather than Unknown — otherwise the whole
            // recommendation for this setting would sit permanently in "could not determine".
            return string.Equals(_fallbackDefaultValue, _appliedValue, StringComparison.OrdinalIgnoreCase)
                ? TweakState.Applied
                : TweakState.NotApplied;
        }

        return string.Equals(current, _appliedValue, StringComparison.OrdinalIgnoreCase)
            ? TweakState.Applied
            : TweakState.NotApplied;
    }

    public void Apply()
    {
        string? current = ReadCurrentValue();

        // "absent" is recorded distinctly from a value that happens to equal the default, so Revert
        // can remove the value entirely and leave the key exactly as Windows shipped it.
        _backupStore.Save(BackupKey, current ?? "\0absent");

        WriteValue(_appliedValue);
    }

    public void Revert()
    {
        string? saved = _backupStore.TryGet(BackupKey);
        if (saved is null)
        {
            return;
        }

        if (saved == "\0absent")
        {
            DeleteValue();
        }
        else
        {
            WriteValue(saved);
        }

        _backupStore.Remove(BackupKey);
    }

    private string? ReadCurrentValue()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
        using RegistryKey? key = baseKey.OpenSubKey(_subKeyPath);
        return key?.GetValue(_valueName) as string;
    }

    private void WriteValue(string value)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
        using RegistryKey key = baseKey.CreateSubKey(_subKeyPath, writable: true);
        key.SetValue(_valueName, value, RegistryValueKind.String);
    }

    private void DeleteValue()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
        using RegistryKey? key = baseKey.OpenSubKey(_subKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
