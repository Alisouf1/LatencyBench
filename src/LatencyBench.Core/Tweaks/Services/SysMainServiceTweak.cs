using System;
using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Services;

public sealed class SysMainServiceTweak : ITweak
{
    private const string ServiceName = "SysMain";

    private const string ServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\SysMain";

    private const string BackupKey = "service:SysMain\\Start";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15.0);

    private readonly TweakBackupStore _backupStore;

    public TweakDefinition Definition { get; } = new TweakDefinition
    {
        Id = "aggressive.sysmain-disable",
        Category = TweakCategory.Aggressive,
        Name = "Disable SysMain (Superfetch)",
        Description = "Stops and disables the SysMain service. Removes its background disk and memory activity, which can show up as occasional DPC spikes on storage drivers. SysMain was designed to speed up app launches on mechanical drives, so on an SSD system the trade is usually worth it — but it is not free: expect slower first launches of large applications.",
        Risk = TweakRisk.Aggressive
    };

    public SysMainServiceTweak(TweakBackupStore backupStore)
    {
        _backupStore = backupStore;
    }

    public TweakState GetState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.StartType == ServiceStartMode.Disabled && controller.Status == ServiceControllerStatus.Stopped
                ? TweakState.Applied
                : TweakState.NotApplied;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // InvalidOperationException covers "no such service" (some Windows editions and LTSC images
            // do not ship SysMain); Win32Exception covers being denied access to query it.
            return TweakState.Unknown;
        }
    }

    public void Apply()
    {
        // Record what the start mode actually was. Revert used to assume Automatic unconditionally,
        // so reverting on a machine where SysMain had been set to Manual — or was already Disabled by
        // the user or an image policy — silently turned the service back on against their wishes.
        _backupStore.Save(BackupKey, ReadStartValue().ToString());

        using (var controller = new ServiceController(ServiceName))
        {
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, StatusTimeout);
            }
        }

        WriteStartValue(ServiceStartValueDisabled);
    }

    public void Revert()
    {
        string? saved = _backupStore.TryGet(BackupKey);
        if (saved is null)
        {
            // SysMain was already disabled before LatencyBench touched it; enabling it now would be a
            // change the user never asked for.
            return;
        }

        if (!int.TryParse(saved, out int previousStartValue))
        {
            throw new InvalidOperationException($"The saved backup for '{Definition.Name}' is invalid.");
        }

        WriteStartValue(previousStartValue);

        // Only start the service again if it was in a start-on-boot mode to begin with. Restoring
        // Manual (3) means "available on demand", not "running now".
        if (previousStartValue is ServiceStartValueAutomatic or ServiceStartValueAutomaticDelayed)
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, StatusTimeout);
            }
        }

        _backupStore.Remove(BackupKey);
    }

    private const int ServiceStartValueAutomaticDelayed = 1;

    private const int ServiceStartValueAutomatic = 2;

    private const int ServiceStartValueDisabled = 4;

    private static int ReadStartValue()
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath)
            ?? throw new InvalidOperationException($"Service '{ServiceName}' registry key not found.");
        return key.GetValue("Start") is int start
            ? start
            : throw new InvalidOperationException($"Service '{ServiceName}' has no Start value to back up.");
    }

    private static void WriteStartValue(int startValue)
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Service '{ServiceName}' registry key not found.");
        key.SetValue("Start", startValue, RegistryValueKind.DWord);
    }
}
