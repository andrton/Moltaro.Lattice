using Moltaro.Lattice.Storage.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// The slots of the text table lie in one managed array that grows by doubling when every slot of it was
// given out (Storage.Text.TextTable). The growth has a bound, the longest array of .NET (Array.MaxLength), and
// a limit of the structure is never a reason to cut the array short: a table whose array cannot grow
// refuses the write that needs a new slot with the refusal the engine has for memory, WriteRejected with
// Limit Memory, before it takes anything, and keeps every slot it has. No option and no new error comes
// with it. No test can write a billion texts, so this one reads the growth function alone, from an empty
// table to the last length and at the boundary where a doubling no longer fits an int.
public sealed class SlotGrowthTests
{
    [Fact]
    public void Array_of_slots_grows_from_empty_without_ever_getting_shorter_and_refuses_to_grow_beyond_its_last_length()
    {
        var length = 0;
        var growths = 0;

        while (TextTable.TryGrowSlots(length, out var grown))
        {
            Assert.True(grown > length, $"The slots grew from {length} to {grown}.");
            Assert.True(grown <= Array.MaxLength, $"The slots grew from {length} to {grown}, longer than an array can be.");
            Assert.True(length == 0 || grown <= 2L * length, $"The slots grew from {length} to {grown}, more than double.");

            length = grown;
            growths++;

            Assert.True(growths <= 64, "The slots grew without end.");
        }

        Assert.True(length > 0, "An empty table cannot grow.");
        Assert.False(TextTable.TryGrowSlots(Array.MaxLength, out _));
    }

    [Fact]
    public void Slots_at_the_length_whose_double_is_no_int_grow_longer_or_not_at_all()
    {
        const int Boundary = 1 << 30;

        if (TextTable.TryGrowSlots(Boundary, out var grown))
        {
            Assert.InRange(grown, Boundary + 1, Array.MaxLength);
        }
    }
}
