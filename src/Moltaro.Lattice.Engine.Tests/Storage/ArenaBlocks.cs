using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage;

// For the tests of the arena and of the roots in it: a block live at once, and whether an address names a
// block the arena holds. The arena allocates a block prepared and makes it live when a write publishes it;
// a test that wants a live block takes both steps. The arena decides whether an address names a block it
// holds before it reaches anything through the address, and refuses with ArgumentException in Header,
// Payload and Free one that names none: the null address, that of a freed block, one in a segment it does
// not hold, one inside a block or beyond the end of its segment. The tests read that decision through
// Header, which reads nothing through the address.
internal static class ArenaBlocks
{
    // Allocates a block as a write takes one and publishes it, prepared (Arena.TryAllocatePrepared) and
    // then made live (Arena.Publish): its header and payload length count in LiveBytes and the rest of its
    // slot in ClassSlackBytes. False, with the counters unchanged, when the memory limit of the arena
    // refuses it; the other refusals are those of TryAllocatePrepared.
    public static bool TryAllocateLive(Arena arena, int payloadLength, BlockKindEnum kind, RootHandle root, out ArenaAddress address)
    {
        if (!arena.TryAllocatePrepared(payloadLength, kind, root, out address))
        {
            return false;
        }

        arena.Publish(address);

        return true;
    }

    // Whether the address is that of a block of the arena that is allocated and not freed, whatever its
    // state, prepared, live or retired. ObjectDisposedException after the arena is disposed, as from every
    // member of the arena.
    public static bool IsValid(Arena arena, ArenaAddress address)
    {
        try
        {
            arena.Header(address);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
