using System;
using System.Linq;
using LatencyBench.Core.Affinity;

namespace LatencyBench.Tests;

public class AffinityMaskTests
{
    [Fact]
    public void SingleCoreSetsTheMatchingBit()
    {
        Assert.Equal(1UL, AffinityMask.FromCoreIndices(new[] { 0 }));
        Assert.Equal(2UL, AffinityMask.FromCoreIndices(new[] { 1 }));
        Assert.Equal(1UL << 63, AffinityMask.FromCoreIndices(new[] { 63 }));
    }

    [Fact]
    public void EmptySelectionProducesAnEmptyMask()
    {
        // AffinityMask itself has no opinion about whether an empty mask is meaningful; rejecting it
        // is InterruptAffinityService's job, and that separation is deliberate.
        Assert.Equal(0UL, AffinityMask.FromCoreIndices(Array.Empty<int>()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(int.MaxValue)]
    public void RejectsCoreIndicesOutsideTheMaskWidth(int core)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AffinityMask.FromCoreIndices(new[] { core }));
    }

    [Fact]
    public void DuplicateCoresSetTheBitOnce()
    {
        Assert.Equal(1UL, AffinityMask.FromCoreIndices(new[] { 0, 0, 0 }));
    }

    [Fact]
    public void RoundTripsThroughCoreIndices()
    {
        var cores = new[] { 0, 3, 7, 31, 63 };

        ulong mask = AffinityMask.FromCoreIndices(cores);

        Assert.Equal(cores, AffinityMask.ToCoreIndices(mask));
    }

    [Fact]
    public void ToBytesIsLittleEndianEightBytes()
    {
        // The registry value is REG_BINARY and Windows reads it little-endian; getting this backwards
        // would pin a device to entirely the wrong cores.
        byte[] bytes = AffinityMask.ToBytes(1UL);

        Assert.Equal(8, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.All(bytes.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ToBytesPlacesHighCoresInTheLastByte()
    {
        byte[] bytes = AffinityMask.ToBytes(1UL << 63);

        Assert.Equal(0x80, bytes[7]);
        Assert.All(bytes.Take(7), b => Assert.Equal(0, b));
    }

    [Fact]
    public void RoundTripsThroughBytes()
    {
        ulong mask = AffinityMask.FromCoreIndices(new[] { 1, 5, 9, 40 });

        Assert.Equal(mask, AffinityMask.FromBytes(AffinityMask.ToBytes(mask)));
    }

    [Fact]
    public void FromBytesToleratesAShortBuffer()
    {
        // Windows has been observed writing a 4-byte AssignmentSetOverride on some systems.
        Assert.Equal(0x0201UL, AffinityMask.FromBytes(new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public void FromBytesIgnoresBytesBeyondTheMaskWidth()
    {
        byte[] oversized = new byte[16];
        oversized[0] = 0xFF;
        oversized[9] = 0xFF;

        Assert.Equal(0xFFUL, AffinityMask.FromBytes(oversized));
    }

    [Fact]
    public void EmptyMaskHasNoCores()
    {
        Assert.Empty(AffinityMask.ToCoreIndices(0UL));
    }

    [Fact]
    public void AllBitsSetYieldsEveryCore()
    {
        Assert.Equal(64, AffinityMask.ToCoreIndices(ulong.MaxValue).Count);
    }
}
