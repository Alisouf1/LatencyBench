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

	private readonly Func<string, bool?> _readEnable;

	private readonly Action<string, bool> _writeEnable;

	public TweakDefinition Definition { get; }

	public DevicePowerManagementTweak(TweakDefinition definition, Func<IReadOnlyList<string>> instanceIdsProvider, TweakBackupStore backupStore)
		: this(definition, instanceIdsProvider, backupStore, ReadEnable, WriteEnable)
	{
	}

	/// <summary>
	/// Seam for tests: substitutes fake device power-management reads/writes instead of the real WMI
	/// calls, so Apply/Revert can be exercised without touching any real device. Every real caller
	/// uses the constructor above, which always goes through the real MSPower_DeviceEnable WMI class.
	/// </summary>
	public DevicePowerManagementTweak(
		TweakDefinition definition,
		Func<IReadOnlyList<string>> instanceIdsProvider,
		TweakBackupStore backupStore,
		Func<string, bool?> readEnable,
		Action<string, bool> writeEnable)
	{
		Definition = definition;
		_instanceIdsProvider = instanceIdsProvider;
		_backupStore = backupStore;
		_readEnable = readEnable;
		_writeEnable = writeEnable;
	}

	public TweakState GetState()
	{
		List<bool> list = (from v in _instanceIdsProvider().Select(_readEnable)
			where v.HasValue
			select v.Value).ToList();
		if (list.Count == 0)
		{
			return TweakState.Unknown;
		}
		return (!list.All((bool v) => !v)) ? TweakState.NotApplied : TweakState.Applied;
	}

	public void Apply()
	{
		foreach (string item in _instanceIdsProvider())
		{
			bool? enabled = _readEnable(item);
			if (!enabled.HasValue)
			{
				// Proceeding without knowing this device's current state would repeat the exact bug
				// Revert below was fixed for: if power management was actually on, that fact would be
				// silently lost here and Revert could never turn it back on.
				throw new InvalidOperationException(
					$"Could not determine the current power-management state for device '{item}', so " +
					"applying this tweak would risk being unable to revert it later.");
			}

			if (enabled.Value)
			{
				// Power management was on before LatencyBench touched it. Record that so Revert can
				// turn it back on instead of leaving it force-disabled forever.
				_backupStore.Save(BackupKey(item), "true");
			}

			_writeEnable(item, false);
		}
	}

	public void Revert()
	{
		foreach (string item in _instanceIdsProvider())
		{
			string? text = _backupStore.TryGet(BackupKey(item));
			if (text is null)
			{
				// No backup means power management was already off for this device before Apply ran
				// (Apply only records one when it finds the device previously on) — there is nothing
				// to restore, and turning it back on here would be a change nobody asked for.
				continue;
			}

			_writeEnable(item, bool.Parse(text));
			_backupStore.Remove(BackupKey(item));
		}
	}

	private static string BackupKey(string instanceId)
	{
		return "devpower:" + instanceId;
	}

	private static bool? ReadEnable(string instanceId)
	{
		ManagementObject? managementObject = FindManagementObject(instanceId);
		try
		{
			return managementObject?["Enable"] as bool?;
		}
		finally
		{
			managementObject?.Dispose();
		}
	}

	private static void WriteEnable(string instanceId, bool value)
	{
		ManagementObject? managementObject = FindManagementObject(instanceId);
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
			managementObject?.Dispose();
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
