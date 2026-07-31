using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Network;

public sealed class NagleAlgorithmTweak : ITweak
{
	private const string InterfacesBasePath = "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces";

	private readonly TweakBackupStore _backupStore;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "network.disable-nagle",
		Category = TweakCategory.Network,
		Name = "Disable Nagle's algorithm",
		Description = "Sets TcpAckFrequency=1 and TCPNoDelay=1 on every network adapter, sending small packets immediately instead of batching them — lower latency, slightly more network overhead.",
		Risk = TweakRisk.Safe
	};

	public NagleAlgorithmTweak(TweakBackupStore backupStore)
	{
		_backupStore = backupStore;
	}

	public TweakState GetState()
	{
		List<string> list = GetAdapterKeyPaths().ToList();
		if (list.Count == 0)
		{
			return TweakState.Unknown;
		}
		return (!list.All(IsApplied)) ? TweakState.NotApplied : TweakState.Applied;
	}

	public void Apply()
	{
		foreach (string adapterKeyPath in GetAdapterKeyPaths())
		{
			using RegistryKey registryKey = Registry.LocalMachine.CreateSubKey(adapterKeyPath, writable: true);
			StashIfPresent(registryKey, adapterKeyPath, "TcpAckFrequency");
			StashIfPresent(registryKey, adapterKeyPath, "TCPNoDelay");
			registryKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
			registryKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
		}
	}

	public void Revert()
	{
		foreach (string adapterKeyPath in GetAdapterKeyPaths())
		{
			using RegistryKey key = Registry.LocalMachine.CreateSubKey(adapterKeyPath, writable: true);
			RestoreOrRemove(key, adapterKeyPath, "TcpAckFrequency");
			RestoreOrRemove(key, adapterKeyPath, "TCPNoDelay");
		}
	}

	private static IEnumerable<string> GetAdapterKeyPaths()
	{
		return from nic in NetworkInterface.GetAllNetworkInterfaces()
			where nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
			select "SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\" + nic.Id;
	}

	private static bool IsApplied(string keyPath)
	{
		using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey(keyPath);
		if (registryKey is null)
		{
			return false;
		}

		return registryKey.GetValue("TcpAckFrequency") is 1 && registryKey.GetValue("TCPNoDelay") is 1;
	}

	private void StashIfPresent(RegistryKey key, string keyPath, string valueName)
	{
		if (key.GetValue(valueName) is int num)
		{
			_backupStore.Save(BackupKey(keyPath, valueName), num.ToString());
		}
		else
		{
			_backupStore.Save(BackupKey(keyPath, valueName), "absent");
		}
	}

	private void RestoreOrRemove(RegistryKey key, string keyPath, string valueName)
	{
		string? text = _backupStore.TryGet(BackupKey(keyPath, valueName));
		int result;
		if ((text == null || text == "absent") ? true : false)
		{
			key.DeleteValue(valueName, throwOnMissingValue: false);
		}
		else if (int.TryParse(text, out result))
		{
			key.SetValue(valueName, result, RegistryValueKind.DWord);
		}
		_backupStore.Remove(BackupKey(keyPath, valueName));
	}

	private static string BackupKey(string keyPath, string valueName)
	{
		return "nagle:" + keyPath + "\\" + valueName;
	}
}
