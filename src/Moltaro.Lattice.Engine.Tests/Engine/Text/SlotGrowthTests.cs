using Moltaro.Lattice.Storage.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Text IDs remain nonzero and addressable through the previous supported
// entry bound. The end of the radix space must refuse without wrapping.
public sealed class SlotGrowthTests
{
    [Fact]
    public void The_first_and_last_resident_text_IDs_fit_without_wrapping()
    {
        Assert.False(TextLocator.TryIdAt(0, 0, out var reserved));
        Assert.Equal(0u, reserved);
        Assert.True(TextLocator.TryIdAt(0, 1, out var first));
        Assert.Equal(1u, first);
        Assert.True(TextLocator.MostId >= Array.MaxLength);
        Assert.True(TextLocator.TryIdAt(TextLocator.MostId >> 6, 62, out var beforeLast));
        Assert.Equal(TextLocator.MostId - 1, beforeLast);
        Assert.True(TextLocator.TryIdAt(TextLocator.MostId >> 6, 63, out var last));
        Assert.Equal(TextLocator.MostId, last);
    }

    [Fact]
    public void A_cell_beyond_the_locator_refuses_before_producing_an_ID()
    {
        Assert.False(TextLocator.TryIdAt((TextLocator.MostId >> 6) + 1, 0, out var afterLast));
        Assert.Equal(0u, afterLast);
        Assert.False(TextLocator.TryIdAt(TextLocator.MostId >> 6, 64, out var overflow));
        Assert.Equal(0u, overflow);
        Assert.False(TextLocator.TryIdAt(uint.MaxValue, 63, out var wrapped));
        Assert.Equal(0u, wrapped);
    }
}
