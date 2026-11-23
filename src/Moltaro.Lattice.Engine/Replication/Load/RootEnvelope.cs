using System.Buffers;
using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// The envelope of a root on the wire, before what BlockExport writes: the type id of its entity type
// (UInt32), the sequence of its last mutation (UInt64) and the number of the member of the write batch that
// made it, 0 for a single write (UInt32), little-endian. They belong to the entry of the root, not to its
// blocks (Storage.Roots.RootEntry), so that a child holds the revision its master holds. A root of a load
// and the root of a Write of a transaction carry it, and the root of a forwarded write with the last
// mutation 0 and the member 0.
internal static class RootEnvelope
{
    public const int Size = sizeof(uint) + sizeof(ulong) + sizeof(uint);

    public static void Write(Span<byte> bytes, uint typeId, ulong lastMutation, uint member)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, typeId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[sizeof(uint)..], lastMutation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[(sizeof(uint) + sizeof(ulong))..], member);
    }

    public static void Write(IBufferWriter<byte> output, uint typeId, ulong lastMutation, uint member)
    {
        Write(output.GetSpan(Size), typeId, lastMutation, member);
        output.Advance(Size);
    }

    // Reads the envelope, with the input advanced past it; false when the input ends before it does.
    public static bool TryRead(ref SequenceReader<byte> input, out uint typeId, out ulong lastMutation, out uint member)
    {
        if (!input.TryReadLittleEndian(out int id) || !input.TryReadLittleEndian(out long mutation) || !input.TryReadLittleEndian(out int number))
        {
            typeId = 0;
            lastMutation = 0;
            member = 0;

            return false;
        }

        typeId = (uint)id;
        lastMutation = (ulong)mutation;
        member = (uint)number;

        return true;
    }
}
