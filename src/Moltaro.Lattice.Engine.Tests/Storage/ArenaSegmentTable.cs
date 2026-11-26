using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Storage;

internal static class ArenaSegmentTable
{
    public static ReadOnlySpan<ArenaSegment?> Slots(Arena arena) => Segments(arena);

    public static ArenaSegment? At(Arena arena, uint index) => Slots(arena)[(int)index];

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_segments")]
    private static extern ref ArenaSegment?[] Segments(Arena arena);
}
