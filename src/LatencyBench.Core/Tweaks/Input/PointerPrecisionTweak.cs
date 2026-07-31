using System;
using System.Linq;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Input;

public sealed class PointerPrecisionTweak : ITweak
{
	private const string SubKeyPath = "Control Panel\\Mouse";

	private static readonly (string Name, string AppliedValue, string WindowsDefault)[] Values = new(string, string, string)[3]
	{
		("MouseSpeed", "0", "1"),
		("MouseThreshold1", "0", "6"),
		("MouseThreshold2", "0", "10")
	};

	private readonly TweakBackupStore _backupStore;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "mouse.pointer-precision",
		Category = TweakCategory.Mouse,
		Name = "Disable pointer acceleration",
		Description = "Turns off \"Enhance pointer precision\" so mouse movement is 1:1 with no OS-level acceleration curve.",
		Risk = TweakRisk.Safe
	};

	public PointerPrecisionTweak(TweakBackupStore backupStore)
	{
		_backupStore = backupStore;
	}

	public TweakState GetState()
	{
		RegistryKey? key = Registry.CurrentUser.OpenSubKey("Control Panel\\Mouse");
		try
		{
			if (key == null)
			{
				return TweakState.Unknown;
			}
			return (!Values.All(((string Name, string AppliedValue, string WindowsDefault) v) => key.GetValue(v.Name) as string == v.AppliedValue)) ? TweakState.NotApplied : TweakState.Applied;
		}
		finally
		{
			if (key != null)
			{
				((IDisposable)key).Dispose();
			}
		}
	}

	public void Apply()
	{
		using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Control Panel\\Mouse", writable: true);
		(string, string, string)[] values = Values;
		for (int i = 0; i < values.Length; i++)
		{
			var (text, text2, _) = values[i];
			if (registryKey.GetValue(text) is string text3 && text3 != text2)
			{
				_backupStore.Save(BackupKey(text), text3);
			}
			registryKey.SetValue(text, text2, RegistryValueKind.String);
		}
	}

	public void Revert()
	{
		using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Control Panel\\Mouse", writable: true);
		(string, string, string)[] values = Values;
		for (int i = 0; i < values.Length; i++)
		{
			(string, string, string) tuple = values[i];
			string item = tuple.Item1;
			string item2 = tuple.Item3;
			string value = _backupStore.TryGet(BackupKey(item)) ?? item2;
			registryKey.SetValue(item, value, RegistryValueKind.String);
			_backupStore.Remove(BackupKey(item));
		}
	}

	private static string BackupKey(string valueName)
	{
		return "registry:HKCU\\Control Panel\\Mouse\\" + valueName;
	}
}
