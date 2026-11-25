namespace Moltaro.Lattice.Access.Views;

// The resident parts of one record, borrowed inside the entity's read epoch. A
// descriptor is relative to the complete block payload. A packed record has no
// descriptors and starts at zero in its first part. See docs/design/arena-protocol.md.
internal readonly ref struct ViewRecord(object layout, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> tail, int recordStart)
{
    public object Layout { get; } = layout;

    public ReadOnlySpan<byte> Bytes { get; } = bytes;

    public ReadOnlySpan<byte> Tail { get; } = tail;

    public int RecordStart { get; } = recordStart;
}
