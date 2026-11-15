using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Allocation;

// Where the arena obtains the native memory of a segment and where it returns it: the native
// allocator, through NativeMemory, aligned to ArenaSegment.Alignment.
//
// It is the one place where the arena obtains and returns native memory, and the tests of the
// engine replace it under exception X3: a test passes the arena a derived class that counts what
// is obtained and returned and can fail a request, so that the return of native memory and a
// failure of the native allocator can be tested deterministically. The arena of the database
// always uses this class itself; nothing in ArenaOptions or in the public API reaches it. The
// arena calls it only when it obtains or releases a segment, never for a block, so its virtual
// calls cost nothing when a block is allocated or freed.
internal unsafe class NativeAllocator
{
    // Obtains size bytes, aligned to ArenaSegment.Alignment and not initialized. When the native
    // allocator cannot supply them, OutOfMemoryException.
    public virtual nint Allocate(int size) =>
        (nint)NativeMemory.AlignedAlloc((nuint)size, ArenaSegment.Alignment);

    // Returns memory that Allocate gave out, once.
    public virtual void Free(nint memory) =>
        NativeMemory.AlignedFree((void*)memory);
}
