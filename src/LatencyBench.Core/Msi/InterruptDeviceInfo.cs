using LatencyBench.Core.Models;

namespace LatencyBench.Core.Msi;

public sealed record InterruptDeviceInfo(string InstanceId, string FriendlyName, string CategoryLabel, bool IsMsiEnabled, InterruptPriority Priority, string? ServiceName = null);
