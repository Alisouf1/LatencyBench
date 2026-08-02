using System.Collections.Generic;

namespace LatencyBench.Core.Models;

public sealed class UsbDeviceNode
{
    public required string InstanceId { get; init; }

    public required string FriendlyName { get; init; }

    public string? DeviceClass { get; init; }

    public string? VendorId { get; init; }

    public string? ProductId { get; init; }

    public int? PortNumber { get; init; }

    public bool IsHub { get; init; }

    public bool IsHostController { get; init; }

    public bool IsWorking { get; init; }

    public UsbSpeed Speed { get; init; } = UsbSpeed.Unknown;

    public List<UsbDeviceNode> Children { get; } = new List<UsbDeviceNode>();

    public string? VidPidLabel => (VendorId == null) ? null : ((ProductId == null) ? ("VID_" + VendorId) : ("VID_" + VendorId + "&PID_" + ProductId));
}
