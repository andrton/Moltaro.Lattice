using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal readonly struct ManagedAccess(RootEntry[][] pages, int count) : IRootAccess
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref RootEntry Get(int index)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ref pages[index >> TableGeometry.PageShift][index & TableGeometry.PageMask];
    }
}
