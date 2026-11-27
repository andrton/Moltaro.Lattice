using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The queue of what a database retired and the items in it, for the tests of the protection that look
// at what is retired and in which order.
internal static class RetiredQueueItems
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
    public static extern ref RetiredQueue RetiredItems(RetiredMemory retired);

    // The items of the retirement queue in their order, copied out of its ring.
    public static RetiredItem[] ItemsOf(RetiredQueue queue)
    {
        var items = new RetiredItem[queue.Count];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = queue[index];
        }

        return items;
    }
}
