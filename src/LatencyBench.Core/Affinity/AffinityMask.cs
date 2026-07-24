namespace LatencyBench.Core.Affinity;

/// <summary>
/// Converts between a set of CPU core indices and the bitmask representation Windows expects
/// for the "AssignmentSetOverride" REG_BINARY value (little-endian, bit N = core N).
/// </summary>
public static class AffinityMask
{
    public const int MaxCores = 64;

    public static ulong FromCoreIndices(IEnumerable<int> coreIndices)
    {
        ulong mask = 0;
        foreach (var core in coreIndices)
        {
            if (core < 0 || core >= MaxCores)
            {
                throw new ArgumentOutOfRangeException(nameof(coreIndices), core, $"Core index must be between 0 and {MaxCores - 1}.");
            }

            mask |= 1UL << core;
        }

        return mask;
    }

    public static IReadOnlyList<int> ToCoreIndices(ulong mask)
    {
        var cores = new List<int>();
        for (var i = 0; i < MaxCores; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                cores.Add(i);
            }
        }

        return cores;
    }

    public static byte[] ToBytes(ulong mask)
    {
        var bytes = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(mask >> (i * 8));
        }

        return bytes;
    }

    public static ulong FromBytes(byte[] bytes)
    {
        ulong mask = 0;
        for (var i = 0; i < Math.Min(bytes.Length, 8); i++)
        {
            mask |= (ulong)bytes[i] << (i * 8);
        }

        return mask;
    }
}
