using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Input;

/// <summary>
/// Shared implementation for the Control Panel input tweaks, which set a small group of REG_SZ values
/// under HKEY_CURRENT_USER.
///
/// <para>
/// PointerPrecisionTweak and KeyboardRepeatRateTweak were byte-for-byte the same logic with a
/// different key path and value list - the same read-all-to-decide-state, the same
/// stash-then-write, the same restore-or-fall-back-to-the-Windows-default. Two copies means a fix to
/// one silently leaves the other wrong, which is exactly the kind of divergence that produced the
/// InterruptDeviceService/InterruptAffinityService split found earlier in this codebase.
/// </para>
///
/// <para>
/// Unlike the network tweaks, revert here always writes rather than deleting. These values are part
/// of a default Windows profile, so removing them is not a state Windows itself produces; restoring
/// the documented default is.
/// </para>
/// </summary>
/// <remarks>
/// The two registry operations are protected virtual so the orchestration can be tested without
/// touching the user's real Control Panel settings. Same pattern as PowerCfgRunner,
/// SysMainServiceTweak and NagleAlgorithmTweak.
/// </remarks>
public abstract class ControlPanelStringTweak : ITweak
{
    /// <summary>One managed value: its name, what this tweak sets it to, and what Windows ships.</summary>
    /// <param name="Name">The REG_SZ value name under <see cref="SubKeyPath"/>.</param>
    /// <param name="AppliedValue">The value that means "this tweak is applied".</param>
    /// <param name="WindowsDefault">Restored when nothing was recorded, so revert never has to guess.</param>
    protected readonly record struct ManagedValue(string Name, string AppliedValue, string WindowsDefault);

    private readonly TweakBackupStore _backupStore;

    protected ControlPanelStringTweak(TweakBackupStore backupStore) => _backupStore = backupStore;

    public abstract TweakDefinition Definition { get; }

    /// <summary>The HKCU-relative key, e.g. <c>Control Panel\Mouse</c>.</summary>
    protected abstract string SubKeyPath { get; }

    protected abstract IReadOnlyList<ManagedValue> Values { get; }

    public TweakState GetState()
    {
        // Every managed value has to match, not just one: half-applied is not applied. A missing
        // value reads as null, which never equals the applied string, so an absent key or an absent
        // value both correctly report NotApplied rather than throwing.
        IReadOnlyDictionary<string, string?> current = ReadValues();
        if (current.Count == 0)
        {
            return TweakState.Unknown;
        }

        return Values.All(v => current.GetValueOrDefault(v.Name) == v.AppliedValue)
            ? TweakState.Applied
            : TweakState.NotApplied;
    }

    public void Apply()
    {
        IReadOnlyDictionary<string, string?> current = ReadValues();

        foreach (ManagedValue value in Values)
        {
            // Only record a value that differs from what is about to be written. Recording the
            // applied value as the "previous" one would make a later revert restore the very thing
            // it was meant to undo.
            string? existing = current.GetValueOrDefault(value.Name);
            if (existing is not null && existing != value.AppliedValue)
            {
                _backupStore.Save(BackupKey(value.Name), existing);
            }

            WriteValue(value.Name, value.AppliedValue);
        }
    }

    public void Revert()
    {
        foreach (ManagedValue value in Values)
        {
            WriteValue(value.Name, _backupStore.TryGet(BackupKey(value.Name)) ?? value.WindowsDefault);
            _backupStore.Remove(BackupKey(value.Name));
        }
    }

    /// <summary>
    /// Every managed value's current content, with null for one that is absent. An empty dictionary
    /// means the key itself could not be opened, which is reported as Unknown rather than guessed at.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?> ReadValues()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKeyPath);
        if (key is null)
        {
            return new Dictionary<string, string?>();
        }

        return Values.ToDictionary(
            v => v.Name,
            v => key.GetValue(v.Name) as string,
            StringComparer.Ordinal);
    }

    protected virtual void WriteValue(string valueName, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SubKeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    private string BackupKey(string valueName) => $@"registry:HKCU\{SubKeyPath}\{valueName}";
}
