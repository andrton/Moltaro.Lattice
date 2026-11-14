using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

// Borrowed only while the owning table remains alive; used synchronously in this experiment.
internal readonly unsafe struct NativeAccess(nint[] pages, int count) : IRootAccess
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref RootEntry Get(int index)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var page = (RootEntry*)pages[index >> TableGeometry.PageShift];
        return ref page[index & TableGeometry.PageMask];
    }
}
