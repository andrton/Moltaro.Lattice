using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed record CollectionNativeSlot(ArenaAddress Address, ArenaSegment? Grant, int NativeBytes, int PayloadBytes,
    bool Allocated, bool Retired, bool Prepared, ArenaAddress Parent)
{
    public bool SameGrantAs(CollectionNativeSlot old) => Allocated && Grant == old.Grant &&
        Address == old.Address && NativeBytes == old.NativeBytes;
}
