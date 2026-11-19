using System.Buffers;
using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// The envelope of a root on the wire, before what BlockExport writes: the type id of its entity type
// (UInt32) and the sequence of its last mutation (UInt64), little-endian. They belong to the entry of the
// root, not to its blocks. A root of a load and the root of a Write of a transaction carry it, and the root
// of a forwarded write with the last mutation 0.
internal static class RootEnvelope
{
    public const int Size = sizeof(uint) + sizeof(ulong);

    public static void Write(Span<byte> bytes, uint typeId, ulong lastMutation)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[sizeof(uint)..], lastMutation);
    }

    public static void Write(IBufferWriter<byte> output, uint typeId, ulong lastMutation)
    {
        Write(output.GetSpan(Size), typeId, lastMutation);
        output.Advance(Size);
    }

    // Reads the envelope, with the input advanced past it; false when the input ends before it does.
    public static bool TryRead(ref SequenceReader<byte> input, out uint typeId, out ulong lastMutation)
    {
        if (!input.TryReadLittleEndian(out int id) || !input.TryReadLittleEndian(out long mutation))
        {
            typeId = 0;
            lastMutation = 0;

            return false;
        }

        typeId = (uint)id;
        lastMutation = (ulong)mutation;

        return true;
    }
}
