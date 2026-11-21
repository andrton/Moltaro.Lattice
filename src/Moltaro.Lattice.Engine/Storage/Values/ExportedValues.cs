using System.Buffers;

namespace Moltaro.Lattice.Storage.Values;

// The String and Binary values of a root for a forwarded write that do not lie inline (RootWriter, "A
// root for a forwarded write"): the bytes each would have in its blocks, kept in managed memory by the
// index of its descriptor until the writer exports the root, then written in the order of the
// descriptors, as a stored root travels (Replication.Load.BlockExport). A value is kept in pieces of the
// payload of a chunk, the places the source fills for a chunked value, so that a long value lies in no
// array of the large object heap and text of UTF-16 is never split inside a code unit.
//
// One writer uses one store, on the thread that writes the root.
internal sealed class ExportedValues
{
    // The pieces of each value, by the index of its descriptor; null for a descriptor that holds none.
    private readonly List<byte[]>?[] _values;

    public ExportedValues(int descriptorCount)
    {
        _values = new List<byte[]>?[descriptorCount];
    }

    // The bytes of every value kept.
    public long Length { get; private set; }

    // Keeps the value of the descriptor at the index, length bytes that the source fills.
    public void Store(int index, int length, ref ValueSource source)
    {
        var pieces = new List<byte[]>((length + ValueBlocks.ChunkPayload - 1) / ValueBlocks.ChunkPayload);

        for (var left = length; left > 0; left -= ValueBlocks.ChunkPayload)
        {
            var piece = new byte[Math.Min(left, ValueBlocks.ChunkPayload)];

            source.Fill(piece);
            pieces.Add(piece);
        }

        _values[index] = pieces;
        Length += length;
    }

    // Writes the value of the descriptor at the index into the output, when one is kept for it.
    public void WriteTo(int index, IBufferWriter<byte> output)
    {
        if (_values[index] is not { } pieces)
        {
            return;
        }

        foreach (var piece in pieces)
        {
            output.Write(piece);
        }
    }
}
