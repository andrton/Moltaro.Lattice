using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// A relative address of the arena (milestone contract, "Native layout and allocation"): the
// segment, a UInt32 slot of the segment table, and the UInt32 byte offset in that segment, packed
// into one UInt64 with the segment in the high 32 bits. Address zero is null, and so is the
// default value. Packing checks nothing; only the arena can tell whether a block is there.
public sealed class ArenaAddressTests
{
    [Fact(Skip = NotImplemented.Arena)]
    public void An_address_packs_the_segment_into_the_high_and_the_offset_into_the_low_32_bits()
    {
        var address = new ArenaAddress(3, 160);

        Assert.Equal(3u, address.Segment);
        Assert.Equal(160u, address.Offset);
        Assert.Equal(0x0000_0003_0000_00A0ul, address.Value);

        var mixed = new ArenaAddress(0x89AB_CDEF, 0x0123_4567);

        Assert.Equal(0x89AB_CDEFu, mixed.Segment);
        Assert.Equal(0x0123_4567u, mixed.Offset);
        Assert.Equal(0x89AB_CDEF_0123_4567ul, mixed.Value);

        var largest = new ArenaAddress(uint.MaxValue, uint.MaxValue);

        Assert.Equal(uint.MaxValue, largest.Segment);
        Assert.Equal(uint.MaxValue, largest.Offset);
        Assert.Equal(ulong.MaxValue, largest.Value);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void The_default_address_is_null_and_so_is_segment_0_at_offset_0()
    {
        var none = default(ArenaAddress);

        Assert.True(none.IsNull);
        Assert.Equal(0ul, none.Value);
        Assert.Equal(0u, none.Segment);
        Assert.Equal(0u, none.Offset);

        Assert.True(new ArenaAddress(0, 0).IsNull);
        Assert.Equal(none, new ArenaAddress(0, 0));
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void Only_the_address_zero_is_null()
    {
        Assert.False(new ArenaAddress(0, 32).IsNull);
        Assert.False(new ArenaAddress(1, 0).IsNull);
        Assert.False(new ArenaAddress(uint.MaxValue, uint.MaxValue).IsNull);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void Two_addresses_are_equal_when_they_name_the_same_segment_and_offset()
    {
        var address = new ArenaAddress(3, 160);
        var same = new ArenaAddress(3, 160);

        Assert.True(address.Equals(same));
        Assert.True(address.Equals((object)same));
        Assert.True(address == same);
        Assert.False(address != same);
        Assert.Equal(address.GetHashCode(), same.GetHashCode());

        // Another offset, another segment, or the two exchanged: another address.
        ArenaAddress[] others = [new(3, 176), new(4, 160), new(160, 3), default];

        foreach (var other in others)
        {
            Assert.False(address.Equals(other));
            Assert.False(address.Equals((object)other));
            Assert.False(address == other);
            Assert.True(address != other);
        }

        // Nothing that is not an address equals one.
        Assert.False(address.Equals(null));
        Assert.False(address.Equals((object)address.Value));
    }
}
