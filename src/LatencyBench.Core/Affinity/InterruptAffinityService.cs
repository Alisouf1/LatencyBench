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
    private const string ProcessorGroupUnsupportedMessage =
        "Interrupt affinity is unavailable on this PC because it has more than 64 logical processors. " +
        "LatencyBench does not yet support Windows processor groups, so it will not write a partial affinity mask.";
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
        EnsureProcessorGroupSupport();
        var processorCount = Environment.ProcessorCount;
        return _treeEnumerator.EnumerateHostControllers()
            .Select(node => ReadCurrentPolicy(node.InstanceId, node.FriendlyName, processorCount))
            .ToList();
    }

    /// <summary>Same registry-backed policy read as <see cref="ListHostControllers"/>, for devices outside the USB tree (GPU, storage controller, audio) that come from <see cref="Msi.InterruptDeviceEnumerator"/> instead.</summary>
    public HostControllerInfo ReadPolicy(string instanceId, string friendlyName) =>
        ReadPolicyAfterCheckingSupport(instanceId, friendlyName);

    public void SetSpecifiedCores(string instanceId, IEnumerable<int> coreIndices, InterruptPriority priority = InterruptPriority.High)
    {
        EnsureProcessorGroupSupport();

        var cores = coreIndices as IReadOnlyList<int> ?? coreIndices.ToList();
        ValidateCoreSelection(cores, priority);
        var mask = AffinityMask.FromCoreIndices(cores);

        using var instanceKey = _enumRoot.OpenSubKey(instanceId, writable: true)
            ?? throw new InvalidOperationException($"Device instance '{instanceId}' was not found, or this process is not elevated.");
        using var deviceParamsKey = instanceKey.CreateSubKey(DeviceParametersRelativePath, writable: true);
        using var policyKey = deviceParamsKey.CreateSubKey(@"Interrupt Management\Affinity Policy", writable: true);

        policyKey.SetValue("DevicePolicy", (int)InterruptAffinityPolicy.SpecifiedProcessors, RegistryValueKind.DWord);
        policyKey.SetValue("DevicePriority", (int)priority, RegistryValueKind.DWord);
        policyKey.SetValue("AssignmentSetOverride", AffinityMask.ToBytes(mask), RegistryValueKind.Binary);
    }

    /// <summary>
    /// Rejects the two ways a caller can produce a policy that Windows will honour but the user did
    /// not intend, both of which the registry itself will accept without complaint.
    /// </summary>
    private static void ValidateCoreSelection(IReadOnlyList<int> cores, InterruptPriority priority)
    {
        // An empty selection writes DevicePolicy=SpecifiedProcessors together with an all-zero
        // AssignmentSetOverride: a device told to target the specified processors, with no processor
        // specified. There is no way to express "no core" meaningfully, and the device's interrupts
        // have nowhere valid to land. Clearing the override is the operation the caller actually wants.
        if (cores.Count == 0)
        {
            throw new ArgumentException(
                "An interrupt affinity policy needs at least one core. To remove a device's affinity " +
                "policy, clear the override instead of applying an empty selection.",
                nameof(cores));
        }

        // AffinityMask allows bits 0-63 because that is the width of the mask, not because this
        // machine has that many processors. Pinning a device to a core that does not exist is a
        // configuration the user can never benefit from and may stop the device receiving interrupts.
        var processorCount = Environment.ProcessorCount;
        foreach (var core in cores)
        {
            if (core < 0 || core >= processorCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cores),
                    core,
                    $"This PC has {processorCount} logical processors, so core {core} does not exist.");
            }
        }

        if (!Enum.IsDefined(typeof(InterruptPriority), priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown interrupt priority.");
        }
    }

    public void ClearOverride(string instanceId)
    {
        EnsureProcessorGroupSupport();
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

    private HostControllerInfo ReadPolicyAfterCheckingSupport(string instanceId, string friendlyName)
    {
        EnsureProcessorGroupSupport();
        return ReadCurrentPolicy(instanceId, friendlyName, Environment.ProcessorCount);
    }

    private static void EnsureProcessorGroupSupport()
    {
        if (Environment.ProcessorCount > AffinityMask.MaxCores)
        {
            throw new NotSupportedException(ProcessorGroupUnsupportedMessage);
        }
    }
}
