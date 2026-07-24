using LatencyBench.Core.HidTesting.Models;

namespace LatencyBench.App.ViewModels;

/// <summary>Pairs a testable HID device with a human-readable location so devices of the same generic name (e.g. two "HID-compliant mouse" entries) are distinguishable in the picker.</summary>
public sealed class TestableDeviceOption
{
    public required HidDeviceInfo Device { get; init; }
    public required string DisplayLabel { get; init; }

    /// <summary>The mouse collection of this physical device, if it has one — captured via raw input.</summary>
    public HidDeviceInfo? MouseCollection { get; init; }

    /// <summary>The keyboard collection of this physical device, if it has one — captured via the keyboard hook.</summary>
    public HidDeviceInfo? KeyboardCollection { get; init; }

    /// <summary>The physical port identity ("Controller N, port P") independent of device name — falls back to DisplayLabel when no controller/port could be resolved.</summary>
    public required string PortLocation { get; init; }

    /// <summary>The host controller's durable device instance ID, when resolved — lets a saved result be matched back to the correct physical controller later, even if "Controller N" numbering ever shifts between enumerations.</summary>
    public string? HostControllerInstanceId { get; init; }
}
