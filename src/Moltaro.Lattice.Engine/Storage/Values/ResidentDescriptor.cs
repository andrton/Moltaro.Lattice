namespace Moltaro.Lattice.Storage.Values;

// The internal contract for a descriptor stored in a native block. The logical metadata
// remains fixed after preparation. The location word can move under the writes lock,
// while readers keep the previous target alive through their epoch. The wire still uses
// the canonical format of ValueBlocks. See docs/design/milestone-1-engine-contract.md.
internal static class ResidentDescriptor
{
    // Reads a native slot under the writes lock or a read epoch, acquiring its location.
    public static ValueDescriptor Read(ReadOnlySpan<byte> place) => throw new NotSupportedException();

    // Encodes a native slot that preparation has not made reachable.
    public static void Write(Span<byte> place, in ValueDescriptor descriptor) => throw new NotSupportedException();

    // Releases a new location under the writes lock, once the destination is ready.
    public static void PublishLocation(Span<byte> place, ArenaAddress destination) => throw new NotSupportedException();

    // Reads immutable metadata, using the original block offset even for a copy.
    public static uint ReadLength(ReadOnlySpan<byte> place, int blockOffset) => throw new NotSupportedException();
}
