using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed record CollectionLookupShape(ArenaAddress Root, RootLayout Layout, RootHandle Handle, int DescriptorOffset,
    ArenaAddress Owner, int Active, CollectionNativeSlot[] Blocks)
{
    public CollectionNativeSlot[] Chunks => Blocks.Where(block => block.Address != Owner).ToArray();
}
