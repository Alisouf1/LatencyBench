using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Network;

/// <remarks>
/// Non-sealed, with adapter enumeration and the three registry operations marked protected virtual.
/// Everything else is orchestration over a set of interfaces - what to record before writing, how an
/// absent value differs from a stored one - and that is what needs pinning. Same pattern as
/// PowerCfgRunner and SysMainServiceTweak.
/// </remarks>
public class NagleAlgorithmTweak : ITweak
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

    /// <summary>The two values that together disable Nagle for an interface.</summary>
    private static readonly string[] ValueNames = { "TcpAckFrequency", "TCPNoDelay" };

    public void Apply()
    {
        foreach (string adapterKeyPath in GetAdapterKeyPaths())
        {
            foreach (string valueName in ValueNames)
            {
                StashCurrentValue(adapterKeyPath, valueName);
            }

            foreach (string valueName in ValueNames)
            {
                WriteValue(adapterKeyPath, valueName, 1);
            }
        }
    }

    public void Revert()
    {
        foreach (string adapterKeyPath in GetAdapterKeyPaths())
        {
            foreach (string valueName in ValueNames)
            {
                RestoreOrRemove(adapterKeyPath, valueName);
            }
        }
    }

    /// <summary>
    /// One registry path per non-loopback interface. A seam so the orchestration above can be tested
    /// against a known set of adapters rather than whatever the test machine happens to have.
    /// </summary>
    protected virtual IEnumerable<string> GetAdapterKeyPaths()
    {
        // InterfacesBasePath rather than the same literal spelled out again: the constant was
        // declared and then never referenced, so editing it would have changed nothing.
        return from nic in NetworkInterface.GetAllNetworkInterfaces()
               where nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
               select $@"{InterfacesBasePath}\{nic.Id}";
    }

    /// <summary>The DWORD currently stored, or null when the value or the key is absent.</summary>
    protected virtual int? ReadValue(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    /// <summary>Writes a DWORD, creating the interface key if Windows has not written one yet.</summary>
    protected virtual void WriteValue(string keyPath, string valueName, int value)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    /// <summary>Removes a value, tolerating both the value and the key being absent.</summary>
    protected virtual void DeleteValue(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private bool IsApplied(string keyPath) =>
        ValueNames.All(valueName => ReadValue(keyPath, valueName) == 1);

    /// <summary>
    /// Records what was there before, including the fact that nothing was. "absent" is a distinct
    /// state from any number: reverting has to delete the value Windows never had rather than write
    /// some assumed default into it.
    /// </summary>
    private void StashCurrentValue(string keyPath, string valueName)
    {
        int? current = ReadValue(keyPath, valueName);
        _backupStore.Save(
            BackupKey(keyPath, valueName),
            current?.ToString(CultureInfo.InvariantCulture) ?? "absent");
    }

    private void RestoreOrRemove(string keyPath, string valueName)
    {
        string? saved = _backupStore.TryGet(BackupKey(keyPath, valueName));

        // No backup and "absent" are treated alike: neither gives a value to put back, and in both
        // cases removing what this tweak wrote returns the interface to Windows' own default
        // behaviour. An unparseable backup is deliberately left alone rather than guessed at.
        if (saved is null or "absent")
        {
            DeleteValue(keyPath, valueName);
        }
        else if (int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out int previous))
        {
            WriteValue(keyPath, valueName, previous);
        }

        _backupStore.Remove(BackupKey(keyPath, valueName));
    }

    private static string BackupKey(string keyPath, string valueName)
    {
        return "nagle:" + keyPath + "\\" + valueName;
    }
}
