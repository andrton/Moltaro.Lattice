using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage;

// Whether an address names a block the arena holds, for the tests of the arena and of the roots in it. The
// arena decides it before it reaches anything through an address, and refuses with ArgumentException in
// Header, Payload and Free an address that names no block it holds: the null address, that of a freed
// block, one in a segment it does not hold, one inside a block or beyond the end of its segment. The tests
// read that decision through Header, which reads nothing through the address.
internal static class ArenaBlocks
{
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
