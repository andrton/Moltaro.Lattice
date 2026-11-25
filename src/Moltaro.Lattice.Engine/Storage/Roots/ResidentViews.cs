using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Resolves schema places and collection records against the blocks borrowed from
// the arena. One immutable reader serves the sets of an instance. The operation
// that lends a view holds a read epoch for every address this reader can reach.
// Scalar fields that cross a packed chunk use local scratch for their own bytes.
// See docs/design/arena-protocol.md, "Protected views."
internal sealed class ResidentViews(Arena arena, LatticeStringEncodingEnum encoding) : IViewStorage
{
    public LatticeRecordView Root(RootLayout layout, ArenaAddress address, LatticeRevision revision) =>
        new(this, new ViewRecord(layout.Structure, layout.RootPayload(arena, address), default, 0), layout.EntityType, revision);

    public int ChildPosition(scoped in ViewRecord row, int owned, LatticeField field) =>
        LayoutOf(row).ChildOf(owned, field);

    public bool IsNull(scoped in ViewRecord row, int position)
    {
        var bit = LayoutOf(row).Place(position).NullBit;
        if (bit == StoredField.NoNullBit)
        {
            return false;
        }

        var offset = row.RecordStart + bit / 8;
        var bits = offset < row.Bytes.Length ? row.Bytes[offset] : row.Tail[offset - row.Bytes.Length];
        return (bits & (1 << (bit % 8))) != 0;
    }

    public TValue ReadScalar<TValue>(scoped in ViewRecord row, int position)
    {
        var layout = LayoutOf(row);
        ref readonly var place = ref layout.Place(position);
        var offset = row.RecordStart + place.Offset - layout.Start;

        if (offset >= row.Bytes.Length)
        {
            return StoredScalars.ReadValue<TValue>(row.Tail.Slice(offset - row.Bytes.Length, place.Width));
        }

        var first = row.Bytes[offset..];
        if (first.Length >= place.Width)
        {
            return StoredScalars.ReadValue<TValue>(first[..place.Width]);
        }

        Span<byte> scalar = stackalloc byte[place.Width];
        first.CopyTo(scalar);
        row.Tail[..(place.Width - first.Length)].CopyTo(scalar[first.Length..]);
        return StoredScalars.ReadValue<TValue>(scalar);
    }

    public string ReadString(scoped in ViewRecord row, int position) =>
        StoredText.Read(arena, ValueBlocks.Read(row.Bytes[DescriptorOffset(row, position)..]), row.Bytes, encoding);

    public (int Length, int Parts) ByteShape(scoped in ViewRecord row, int position)
    {
        var descriptor = ValueBlocks.Read(row.Bytes[DescriptorOffset(row, position)..]);
        var length = (int)descriptor.Length;
        return (length, PartCount(length, ValueBlocks.LiesInChunks(descriptor)));
    }

    public ReadOnlySpan<byte> GetPart(ViewRecord row, int position, int part)
    {
        var descriptor = ValueBlocks.Read(row.Bytes[DescriptorOffset(row, position)..]);
        var chunked = ValueBlocks.LiesInChunks(descriptor);
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(part, PartCount((int)descriptor.Length, chunked));

        return chunked
            ? ValueBlocks.Chunk(arena, ValueBlocks.Directory(arena, descriptor), part)
            : ValueBlocks.Contiguous(arena, descriptor, row.Bytes);
    }

    public ViewCollection Collection(ViewRecord row, int position)
    {
        var entry = LayoutOf(row).ElementOf(position);
        var offset = DescriptorOffset(row, position);
        var descriptor = ValueBlocks.Read(row.Bytes[offset..]);
        var entries = descriptor.Kind == DescriptorKindEnum.Lookup ? CollectionLookup.Entries(arena, descriptor) : descriptor;
        return new ViewCollection(entry, row.Bytes, offset, entry.StoredEntryCount(entries));
    }

    public ViewRecord Entry(ViewCollection collection, int ordinal)
    {
        var layout = (StructureLayout)collection.EntryLayout;
        var descriptor = ValueBlocks.Read(collection.Holder[collection.DescriptorOffset..]);
        var entries = new CollectionKeys(arena, layout, descriptor, collection.Holder, encoding);
        var bytes = entries.EntryParts(ordinal, out var tail, out var start);
        return new ViewRecord(layout, bytes, tail, start);
    }

    public ViewPackedBlock ReadPackedListBlock(ViewCollection collection, int block)
    {
        var layout = (StructureLayout)collection.EntryLayout;
        if (layout.HoldsDescriptors)
        {
            return default;
        }

        var descriptor = ValueBlocks.Read(collection.Holder[collection.DescriptorOffset..]);
        var bytes = ValueBlocks.LiesInChunks(descriptor)
            ? ValueBlocks.Chunk(arena, ValueBlocks.Directory(arena, descriptor), block)
            : ValueBlocks.Contiguous(arena, descriptor, collection.Holder);
        return new ViewPackedBlock(bytes, layout.Size);
    }

    public int FindEntry(scoped in ViewCollection collection, in IndexValue key)
    {
        var descriptor = ValueBlocks.Read(collection.Holder[collection.DescriptorOffset..]);
        return CollectionLookup.Find(arena, (StructureLayout)collection.EntryLayout, descriptor, collection.Holder, encoding, key);
    }

    private static int PartCount(int length, bool chunked) => chunked ? ValueBlocks.ChunkCount(length) : 1;

    private static StructureLayout LayoutOf(scoped in ViewRecord row) => (StructureLayout)row.Layout;

    private static int DescriptorOffset(scoped in ViewRecord row, int position)
    {
        var layout = LayoutOf(row);
        return row.RecordStart + layout.Place(position).Offset - layout.Start;
    }
}
