using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.DevicePower;

public sealed class DevicePowerManagementTweak : ITweak
{
	private static readonly Regex TrailingIndexPattern = new Regex("_\\d+$", RegexOptions.Compiled);

	private readonly Func<IReadOnlyList<string>> _instanceIdsProvider;

	private readonly TweakBackupStore _backupStore;

	public TweakDefinition Definition { get; }

	public DevicePowerManagementTweak(TweakDefinition definition, Func<IReadOnlyList<string>> instanceIdsProvider, TweakBackupStore backupStore)
	{
		Definition = definition;
		_instanceIdsProvider = instanceIdsProvider;
		_backupStore = backupStore;
	}

	public TweakState GetState()
	{
		List<bool?> list = (from v in _instanceIdsProvider().Select(ReadEnable)
			where v.HasValue
			select v).ToList();
		if (list.Count == 0)
		{
			return TweakState.Unknown;
		}
		return (!list.All((bool? v) => !v.Value)) ? TweakState.NotApplied : TweakState.Applied;
	}

	public void Apply()
	{
		bool valueOrDefault = default(bool);
		foreach (string item in _instanceIdsProvider())
		{
			bool? flag = ReadEnable(item);
			int num;
			if (flag.HasValue)
			{
				valueOrDefault = flag == true;
				num = 1;
			}
			else
			{
				num = 0;
			}
			if (((uint)num & (valueOrDefault ? 1u : 0u)) != 0)
			{
				_backupStore.Save(BackupKey(item), "true");
			}
			WriteEnable(item, value: false);
		}
	}

	public void Revert()
	{
		foreach (string item in _instanceIdsProvider())
		{
			string text = _backupStore.TryGet(BackupKey(item));
			bool value = text == null || bool.Parse(text);
			WriteEnable(item, value);
			_backupStore.Remove(BackupKey(item));
		}
	}

	private static string BackupKey(string instanceId)
	{
		return "devpower:" + instanceId;
	}

	private static bool? ReadEnable(string instanceId)
	{
		ManagementObject managementObject = FindManagementObject(instanceId);
		try
		{
			return managementObject?["Enable"] as bool?;
		}
		finally
		{
			((IDisposable)managementObject)?.Dispose();
		}
	}

	private static void WriteEnable(string instanceId, bool value)
	{
		ManagementObject managementObject = FindManagementObject(instanceId);
		try
		{
			if (managementObject != null)
			{
				managementObject["Enable"] = value;
				managementObject.Put();
			}
		}
		finally
		{
			((IDisposable)managementObject)?.Dispose();
		}
	}

	private static ManagementObject? FindManagementObject(string cmInstanceId)
	{
		ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM MSPower_DeviceEnable");
		try
		{
			foreach (ManagementObject item in managementObjectSearcher.Get())
			{
				if (item["InstanceName"] is string input && string.Equals(TrailingIndexPattern.Replace(input, ""), cmInstanceId, StringComparison.OrdinalIgnoreCase))
				{
					return item;
				}
				item.Dispose();
			}
			return null;
		}
		finally
		{
			((IDisposable)managementObjectSearcher)?.Dispose();
		}
	}
}
