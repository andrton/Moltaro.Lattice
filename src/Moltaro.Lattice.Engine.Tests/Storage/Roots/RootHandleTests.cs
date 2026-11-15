using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Roots;

// A root handle (milestone contract, "Native layout and allocation"): the UInt32 generation of a
// slot of the root table and the UInt32 slot, one lifetime of one root. Packing checks nothing;
// only the root table can tell whether the lifetime is current. The default handle has
// generation 0, which no slot ever has.
public sealed class RootHandleTests
{
    [Fact(Skip = NotImplemented.Arena)]
    public void A_handle_carries_its_generation_and_its_slot()
    {
        var handle = new RootHandle(7, 42);

        Assert.Equal(7u, handle.Generation);
        Assert.Equal(42u, handle.Slot);

        var largest = new RootHandle(uint.MaxValue, uint.MaxValue);

        Assert.Equal(uint.MaxValue, largest.Generation);
        Assert.Equal(uint.MaxValue, largest.Slot);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void The_default_handle_has_generation_0_and_slot_0()
    {
        var none = default(RootHandle);

        Assert.Equal(0u, none.Generation);
        Assert.Equal(0u, none.Slot);
        Assert.Equal(new RootHandle(0, 0), none);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void Two_handles_are_equal_when_they_name_the_same_slot_with_the_same_generation()
    {
        var handle = new RootHandle(7, 42);
        var same = new RootHandle(7, 42);

        Assert.True(handle.Equals(same));
        Assert.True(handle.Equals((object)same));
        Assert.True(handle == same);
        Assert.False(handle != same);
        Assert.Equal(handle.GetHashCode(), same.GetHashCode());

        // Another lifetime of the same slot, another slot, or the two exchanged: another handle.
        RootHandle[] others = [new(8, 42), new(6, 42), new(7, 43), new(42, 7), default];

        foreach (var other in others)
        {
            Assert.False(handle.Equals(other));
            Assert.False(handle.Equals((object)other));
            Assert.False(handle == other);
            Assert.True(handle != other);
        }

        Assert.False(handle.Equals(null));
    }
}
