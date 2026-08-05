using System;

namespace LatencyBench.Core.Devices;

/// <summary>
/// Validation for Windows device instance IDs before they are used to build a registry path under
/// HKLM\SYSTEM\CurrentControlSet\Enum.
///
/// <para>
/// Why this exists, stated precisely rather than dramatically. The registry does NOT interpret ".."
/// as a parent reference — this was verified empirically, not assumed: opening "..\Secret" relative
/// to a key returns null, and CreateSubKey(@"A\..\B") creates a literal subkey named "..". So a
/// malformed instance ID cannot escape the Enum subtree, and this is not a path-traversal or
/// privilege-escalation defect.
/// </para>
///
/// <para>
/// What it genuinely prevents is worse than it sounds and easy to miss: RegistryKey.CreateSubKey
/// creates every missing level of the path it is given. Passing an instance ID for a device that is
/// not present — a stale ID, an unplugged device, a saved profile from another machine — therefore
/// does not fail. It silently fabricates a device node inside the live Windows device tree, with
/// interrupt-policy values attached to hardware that does not exist. Nothing reports an error, and
/// the junk persists across reboots.
/// </para>
///
/// <para>
/// The format rules follow the documented shape of a device instance ID (see MSDN "Device Instance
/// IDs"): enumerator-prefixed, backslash-separated, at most MAX_DEVICE_ID_LEN characters. Validation
/// is a whitelist rather than a blacklist, because enumerating the characters that are legal is a
/// closed problem while enumerating the ones that are dangerous is not.
/// </para>
/// </summary>
public static class DeviceInstanceId
{
    /// <summary>
    /// MAX_DEVICE_ID_LEN from cfgmgr32.h. Windows itself will not produce an instance ID longer than
    /// this, so anything longer did not come from device enumeration.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// True when <paramref name="instanceId"/> has the shape of an ID produced by device enumeration.
    /// </summary>
    public static bool IsValid(string? instanceId) => Validate(instanceId, out _);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the ID is not well formed, naming the specific
    /// rule that failed so a caller reading the log knows which one it was.
    /// </summary>
    public static void ThrowIfInvalid(string? instanceId, string parameterName)
    {
        if (!Validate(instanceId, out string? reason))
        {
            throw new ArgumentException(
                $"'{instanceId ?? "(null)"}' is not a valid device instance ID: {reason}",
                parameterName);
        }
    }

    private static bool Validate(string? instanceId, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            reason = "it is null, empty, or whitespace.";
            return false;
        }

        if (instanceId.Length > MaxLength)
        {
            reason = $"it is {instanceId.Length} characters; the Windows maximum is {MaxLength}.";
            return false;
        }

        // A leading separator would resolve relative to the Enum root in a way the caller did not
        // write; a trailing one produces an empty final segment.
        if (instanceId[0] == '\\' || instanceId[^1] == '\\')
        {
            reason = "it starts or ends with a backslash.";
            return false;
        }

        // Every real instance ID is ENUMERATOR\DEVICE\INSTANCE - at minimum an enumerator and one
        // more segment. A single segment would target the enumerator key itself, which is shared by
        // every device behind that bus and must never be written to as if it were one device.
        string[] segments = instanceId.Split('\\');
        if (segments.Length < 2)
        {
            reason = "it has no enumerator prefix (expected a form like 'PCI\\VEN_1234&DEV_5678\\3&11583659&0&E0').";
            return false;
        }

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                reason = "it contains an empty path segment (two consecutive backslashes).";
                return false;
            }

            // "." and ".." are legal registry key names, so they would be created as literal keys
            // rather than rejected. They are never produced by device enumeration, so their presence
            // means the value did not come from where the caller believes it did.
            if (segment is "." or "..")
            {
                reason = "it contains a '.' or '..' segment, which device enumeration never produces.";
                return false;
            }

            foreach (char c in segment)
            {
                if (!IsAllowed(c))
                {
                    reason = $"it contains the character '{c}' (0x{(int)c:X2}), which is not valid in a device instance ID.";
                    return false;
                }
            }
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The characters device enumeration actually produces. Deliberately a whitelist: alphanumerics
    /// plus the punctuation seen in real IDs (&amp; in PCI addresses, {} in container GUIDs, and
    /// _ - . # elsewhere). Anything outside this set did not come from CfgMgr32.
    /// </summary>
    private static bool IsAllowed(char c) =>
        (c >= 'A' && c <= 'Z') ||
        (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') ||
        c is '&' or '_' or '-' or '.' or '{' or '}' or '#';
}
