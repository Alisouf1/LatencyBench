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

	private string BackupKey => "powercfg:" + _subgroupGuid + "\\" + _settingGuid;

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
		uint? num = ReadCurrentValue();
		if (num.HasValue)
		{
			uint valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault != _appliedValue)
			{
				_backupStore.Save(BackupKey, valueOrDefault.ToString());
			}
		}
		WriteValueAndVerify(_appliedValue);
	}

	public void Revert()
	{
		string text = _backupStore.TryGet(BackupKey);
		uint result;
		uint value = ((text != null && uint.TryParse(text, out result)) ? result : _fallbackDefaultValue);
		WriteValueAndVerify(value);
		_backupStore.Remove(BackupKey);
	}

	private uint? ReadCurrentValue()
	{
		return _powerCfg.QueryAcValueIndex(_powerCfg.GetActiveSchemeGuid(), _subgroupGuid, _settingGuid);
	}

	private void WriteValueAndVerify(uint value)
	{
		_powerCfg.SetAcValueIndex(_powerCfg.GetActiveSchemeGuid(), _subgroupGuid, _settingGuid, value);
		if (ReadCurrentValue() != value)
		{
			throw new InvalidOperationException("Windows didn't accept the change — this power setting isn't available on this system's active power scheme.");
		}
	}
}
