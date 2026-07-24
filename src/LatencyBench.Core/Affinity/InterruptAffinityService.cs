using Microsoft.Win32;
using LatencyBench.Core.Models;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.Core.Affinity;

/// <summary>
/// Reads and writes Windows' per-device Interrupt Affinity Policy
/// (HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters\Interrupt Management\Affinity Policy).
/// Writes require administrator rights and only take effect after the device is restarted or the machine reboots.
/// </summary>
public sealed class InterruptAffinityService : IDisposable
{
    private const string AffinityPolicyRelativePath = @"Device Parameters\Interrupt Management\Affinity Policy";
    private const string DeviceParametersRelativePath = "Device Parameters";

    /// <summary>Relative to the "Device Parameters" key. Deletes only the affinity policy — NOT the
    /// sibling "MessageSignaledInterruptProperties" (MSI) key under "Interrupt Management", which is
    /// managed by the MSI tab. Deleting the whole "Interrupt Management" tree here would silently
    /// wipe a controller's MSI setting.</summary>
    private const string AffinityPolicySubkeyPath = @"Interrupt Management\Affinity Policy";

    private readonly UsbTreeEnumerator _treeEnumerator;
    private readonly RegistryKey _enumRoot;
    private readonly bool _ownsEnumRoot;

    public InterruptAffinityService(UsbTreeEnumerator? treeEnumerator = null, RegistryKey? enumRoot = null)
    {
        _treeEnumerator = treeEnumerator ?? new UsbTreeEnumerator();
        if (enumRoot is not null)
        {
            _enumRoot = enumRoot;
            _ownsEnumRoot = false;
        }
        else
        {
            _enumRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum")
                ?? throw new InvalidOperationException("Could not open HKLM\\SYSTEM\\CurrentControlSet\\Enum.");
            _ownsEnumRoot = true;
        }
    }

    public void Dispose()
    {
        if (_ownsEnumRoot)
        {
            _enumRoot.Dispose();
        }
    }

    public IReadOnlyList<HostControllerInfo> ListHostControllers()
    {
        var processorCount = Environment.ProcessorCount;
        return _treeEnumerator.EnumerateHostControllers()
            .Select(node => ReadCurrentPolicy(node.InstanceId, node.FriendlyName, processorCount))
            .ToList();
    }

    /// <summary>Same registry-backed policy read as <see cref="ListHostControllers"/>, for devices outside the USB tree (GPU, storage controller, audio) that come from <see cref="Msi.InterruptDeviceEnumerator"/> instead.</summary>
    public HostControllerInfo ReadPolicy(string instanceId, string friendlyName) =>
        ReadCurrentPolicy(instanceId, friendlyName, Environment.ProcessorCount);

    public void SetSpecifiedCores(string instanceId, IEnumerable<int> coreIndices, InterruptPriority priority = InterruptPriority.High)
    {
        var mask = AffinityMask.FromCoreIndices(coreIndices);

        using var instanceKey = _enumRoot.OpenSubKey(instanceId, writable: true)
            ?? throw new InvalidOperationException($"Device instance '{instanceId}' was not found, or this process is not elevated.");
        using var deviceParamsKey = instanceKey.CreateSubKey(DeviceParametersRelativePath, writable: true);
        using var policyKey = deviceParamsKey.CreateSubKey(@"Interrupt Management\Affinity Policy", writable: true);

        policyKey.SetValue("DevicePolicy", (int)InterruptAffinityPolicy.SpecifiedProcessors, RegistryValueKind.DWord);
        policyKey.SetValue("DevicePriority", (int)priority, RegistryValueKind.DWord);
        policyKey.SetValue("AssignmentSetOverride", AffinityMask.ToBytes(mask), RegistryValueKind.Binary);
    }

    public void ClearOverride(string instanceId)
    {
        using var instanceKey = _enumRoot.OpenSubKey(instanceId, writable: true)
            ?? throw new InvalidOperationException($"Device instance '{instanceId}' was not found, or this process is not elevated.");
        using var deviceParamsKey = instanceKey.OpenSubKey(DeviceParametersRelativePath, writable: true);
        deviceParamsKey?.DeleteSubKeyTree(AffinityPolicySubkeyPath, throwOnMissingSubKey: false);
    }

    private HostControllerInfo ReadCurrentPolicy(string instanceId, string friendlyName, int processorCount)
    {
        var info = new HostControllerInfo
        {
            InstanceId = instanceId,
            FriendlyName = friendlyName,
            LogicalProcessorCount = processorCount,
        };

        using var policyKey = _enumRoot.OpenSubKey($@"{instanceId}\{AffinityPolicyRelativePath}");
        if (policyKey is null)
        {
            return info;
        }

        if (policyKey.GetValue("DevicePolicy") is int policyValue && Enum.IsDefined(typeof(InterruptAffinityPolicy), policyValue))
        {
            info.Policy = (InterruptAffinityPolicy)policyValue;
        }

        if (policyKey.GetValue("DevicePriority") is int priorityValue && Enum.IsDefined(typeof(InterruptPriority), priorityValue))
        {
            info.Priority = (InterruptPriority)priorityValue;
        }

        if (policyKey.GetValue("AssignmentSetOverride") is byte[] maskBytes)
        {
            info.AffinityMask = AffinityMask.FromBytes(maskBytes);
        }

        return info;
    }
}
