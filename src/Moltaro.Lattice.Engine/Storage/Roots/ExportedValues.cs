using System.Buffers;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The values of a root for a forwarded write that do not lie inline (RootWriter, "A root for a forwarded
// write"), kept in managed memory as the wire carries them until the writer exports the root, each by the
// descriptor that names it, and written in the order of the descriptors, as a stored root travels
// (Replication.Load.BlockExport):
// - a String, a Binary or a packed List: the bytes it would have in its blocks, kept in pieces of the payload
//   of a chunk, the places the source fills for a chunked value, so that a long value lies in no array of the
//   large object heap and text of UTF-16 is never split inside a code unit;
// - a List of records: the payload of each block of its records, each followed by the values of its records
//   that do not lie inline, in their order, which a store of that block keeps (StoreRecords).
//
// A store serves the structures of one block: the root, whose image the writer fills, or the records of one
// block of a List. The offset of a descriptor in that block, as the holder of the block counts it
// (ValueHolder), tells which descriptor of which structure it is.
//
// One writer uses one store, on the thread that writes the root.
internal sealed class ExportedValues
{
    // The layout of the structures of the block, and where the first one begins as the offsets of the
    // holder count: 0 for a root, whose offsets count from the start of its block with the structure at 32,
    // and the 32 bytes of the header for the records of a block of records.
    private readonly StructureLayout _structure;

    private readonly int _first;

    // How many structures the block holds: 1 for a root, the records of a block of records.
    private readonly int _structures;

    // The pieces of each value, by the index of its descriptor among the descriptors of the block, structure
    // after structure; null for a descriptor that holds none.
    private readonly List<byte[]>?[] _values;

    private ExportedValues(StructureLayout structure, int first, int structures, MemoryLease? lease)
    {
        _structure = structure;
        _first = first;
        _structures = structures;
        Lease = lease;
        var length = structures * structure.DescriptorCount;
        _values = lease?.Allocate<List<byte[]>?>(length, nameof(ExportedValues)) ?? new List<byte[]>?[length];
    }

    public MemoryLease? Lease { get; }

    // The store of a root of the layout.
    public static ExportedValues OfRoot(RootLayout layout, MemoryLease? lease = null) => new(layout.Structure, 0, 1, lease);

    // The store of a block of count records of the element.
    public static ExportedValues OfRecords(StructureLayout element, int count, MemoryLease? lease) => new(element, BlockHeader.Size, count, lease);

    // The bytes of every value kept, the blocks of records and their values included.
    public long Length { get; private set; }

    // Keeps the value of the descriptor at the offset, length bytes that the source fills.
    public void Store<TSource>(int offset, int length, ref TSource source)
        where TSource : IValueSource, allows ref struct
    {
        var pieces = NewPieces((length + ValueBlocks.ChunkPayload - 1) / ValueBlocks.ChunkPayload);

        for (var left = length; left > 0; left -= ValueBlocks.ChunkPayload)
        {
            var piece = NewBlock(Math.Min(left, ValueBlocks.ChunkPayload));

            source.Fill(piece);
            pieces.Add(piece);
        }

        _values[IndexOf(offset)] = pieces;
        Length += length;
    }

    // Keeps the payload of one block of the records of the List whose descriptor is at the offset, and after
    // it the values of those records that the store of the block keeps; the blocks of one List in their
    // order.
    public void StoreRecords(int offset, byte[] block, ExportedValues records)
    {
        var pieces = _values[IndexOf(offset)] ??= NewPieces(0);
        pieces.EnsureCapacity(pieces.Count + 1, Lease?.Budget, nameof(ExportedValues));

        pieces.Add(block);

        foreach (var values in records._values)
        {
            if (values is not null)
            {
                pieces.EnsureCapacity(pieces.Count + values.Count, Lease?.Budget, nameof(ExportedValues));
                pieces.AddRange(values);
            }
        }

        Length += block.Length + records.Length;
    }

    public byte[] NewBlock(int length) =>
        Lease?.Allocate<byte>(length, nameof(ExportedValues)) ?? new byte[length];

    private List<byte[]> NewPieces(int count)
    {
        if (Lease is not { } lease)
        {
            return new List<byte[]>(count);
        }

        var pieces = lease.Allocate(MemoryBudget.ObjectBytes(IntPtr.Size + 2L * sizeof(int)), nameof(ExportedValues),
            static () => new List<byte[]>(), static (budget, values) =>
            {
                budget.Release(BudgetCollections.Items(values));
                values.Clear();
                values.Capacity = 0;
            });
        pieces.EnsureCapacity(count, lease.Budget, nameof(ExportedValues));
        return pieces;
    }

    // Writes every value kept into the output, in the order of the descriptors.
    public void WriteTo(IBufferWriter<byte> output)
    {
        foreach (var values in _values)
        {
            if (values is null)
            {
                continue;
            }

            foreach (var piece in values)
            {
                output.Write(piece);
            }
        }
    }

    // The index of the descriptor at the offset (see the class): the structure it lies in, at the stride of
    // the layout, and its place among the descriptors of that structure. A root is one structure, so the
    // value of a write forwarded by a child of scalar fields takes no division by the stride.
    private int IndexOf(int offset)
    {
        var inBlock = offset - _first;
        var structure = _structures == 1 ? 0 : (inBlock - _structure.Start) / _structure.Size;
        var descriptor = (inBlock - (structure * _structure.Size) - _structure.DescriptorOffset) / ValueBlocks.DescriptorSize;

        return (structure * _structure.DescriptorCount) + descriptor;
    }
}
