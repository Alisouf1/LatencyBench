using System;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks;

public sealed class PowerCfgAcValueTweak : ITweak
{
    private readonly PowerCfgRunner _powerCfg;

    private readonly string _subgroupGuid;

    private readonly string _settingGuid;

    private readonly uint _appliedValue;

    private readonly uint _fallbackDefaultValue;

    private readonly TweakBackupStore _backupStore;

    public TweakDefinition Definition { get; }

    private string BackupSchemeKey => "powercfg-scheme:" + _subgroupGuid + "\\" + _settingGuid;

    private string BackupKey(string schemeGuid) => "powercfg:" + schemeGuid + "\\" + _subgroupGuid + "\\" + _settingGuid;

    public PowerCfgAcValueTweak(TweakDefinition definition, PowerCfgRunner powerCfg, string subgroupGuid, string settingGuid, uint appliedValue, uint fallbackDefaultValue, TweakBackupStore backupStore)
    {
        Definition = definition;
        _powerCfg = powerCfg;
        _subgroupGuid = subgroupGuid;
        _settingGuid = settingGuid;
        _appliedValue = appliedValue;
        _fallbackDefaultValue = fallbackDefaultValue;
        _backupStore = backupStore;
    }

    public TweakState GetState()
    {
        uint? num = ReadCurrentValue();
        if (!num.HasValue)
        {
            return TweakState.Unknown;
        }
        return (num != _appliedValue) ? TweakState.NotApplied : TweakState.Applied;
    }

    public void Apply()
    {
        string schemeGuid = _powerCfg.GetActiveSchemeGuid();

        // When the setting has never been overridden on this scheme there is no stored value to read
        // back, so fall back to the documented Windows default for it. Without this the tweak applies
        // with no backup recorded at all and Revert becomes a silent no-op.
        uint previous = ReadCurrentValue(schemeGuid) ?? _fallbackDefaultValue;
        if (previous != _appliedValue)
        {
            _backupStore.Save(BackupKey(schemeGuid), previous.ToString());
            _backupStore.Save(BackupSchemeKey, schemeGuid);
        }

        WriteValueAndVerify(schemeGuid, _appliedValue);
    }

    public void Revert()
    {
        string? schemeGuid = _backupStore.TryGet(BackupSchemeKey);
        if (schemeGuid == null)
        {
            // The value was already applied before LatencyBench touched it, so there
            // is no known prior value to restore safely.
            return;
        }

        string? text = _backupStore.TryGet(BackupKey(schemeGuid));
        if (!uint.TryParse(text, out uint value))
        {
            throw new InvalidOperationException($"The saved backup for '{Definition.Name}' is invalid.");
        }

        WriteValueAndVerify(schemeGuid, value);
        _backupStore.Remove(BackupKey(schemeGuid));
        _backupStore.Remove(BackupSchemeKey);
    }

    private uint? ReadCurrentValue()
    {
        return ReadCurrentValue(_powerCfg.GetActiveSchemeGuid());
    }

    private uint? ReadCurrentValue(string schemeGuid)
    {
        return _powerCfg.QueryAcValueIndex(schemeGuid, _subgroupGuid, _settingGuid);
    }

    private void WriteValueAndVerify(string schemeGuid, uint value)
    {
        _powerCfg.SetAcValueIndex(schemeGuid, _subgroupGuid, _settingGuid, value);
        if (ReadCurrentValue(schemeGuid) != value)
        {
            throw new InvalidOperationException("Windows didn't accept the change — this power setting isn't available on this system's active power scheme.");
        }
    }
}
