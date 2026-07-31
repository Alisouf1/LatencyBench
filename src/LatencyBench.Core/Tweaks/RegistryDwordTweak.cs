using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks;

public sealed class RegistryDwordTweak : ITweak
{
	private readonly RegistryHive _hive;

	private readonly string _subKeyPath;

	private readonly string _valueName;

	private readonly int _appliedValue;

	private readonly int _fallbackDefaultValue;

	private readonly TweakBackupStore _backupStore;

	public TweakDefinition Definition { get; }

	private string BackupKey => $"registry:{_hive}\\{_subKeyPath}\\{_valueName}";

	public RegistryDwordTweak(TweakDefinition definition, RegistryHive hive, string subKeyPath, string valueName, int appliedValue, int fallbackDefaultValue, TweakBackupStore backupStore)
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
		int? num = ReadCurrentValue();
		if (!num.HasValue)
		{
			return TweakState.Unknown;
		}
		return (num != _appliedValue) ? TweakState.NotApplied : TweakState.Applied;
	}

	public void Apply()
	{
		int? num = ReadCurrentValue();
		if (num.HasValue)
		{
			int valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault != _appliedValue)
			{
				_backupStore.Save(BackupKey, valueOrDefault.ToString());
			}
		}
		else
		{
			// A missing value is a distinct original state.  Remember it so Revert can
			// remove the value instead of replacing it with an assumed Windows default.
			_backupStore.Save(BackupKey, "absent");
		}
		WriteValue(_appliedValue);
	}

	public void Revert()
	{
		string? text = _backupStore.TryGet(BackupKey);
		if (text == null)
		{
			// The setting was already applied before LatencyBench touched it, so there
			// is no known prior value to restore safely.
			return;
		}

		if (text == "absent")
		{
			DeleteValue();
		}
		else if (int.TryParse(text, out int value))
		{
			WriteValue(value);
		}
		else
		{
			throw new InvalidOperationException($"The saved backup for '{Definition.Name}' is invalid.");
		}

		_backupStore.Remove(BackupKey);
	}

	private int? ReadCurrentValue()
	{
		using RegistryKey registryKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
		using RegistryKey? registryKey2 = registryKey.OpenSubKey(_subKeyPath);
		return (registryKey2?.GetValue(_valueName) is int value) ? new int?(value) : ((int?)null);
	}

	private void WriteValue(int value)
	{
		using RegistryKey registryKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
		using RegistryKey registryKey2 = registryKey.CreateSubKey(_subKeyPath, writable: true);
		registryKey2.SetValue(_valueName, value, RegistryValueKind.DWord);
	}

	private void DeleteValue()
	{
		using RegistryKey registryKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
		using RegistryKey? registryKey2 = registryKey.OpenSubKey(_subKeyPath, writable: true);
		registryKey2?.DeleteValue(_valueName, throwOnMissingValue: false);
	}
}
