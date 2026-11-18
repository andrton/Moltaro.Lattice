using System.Buffers.Binary;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The payload of a LoadEnd frame as the tests of Epic 1, sprint S11 assume it, apart from the codec of the
// engine: LoadEnd carries S, R and the number of roots sent (decided after the study of S11, point 4),
// in that order, each a UInt64, little-endian like every number of the transport outside a root: the
// start position S of the capture, the end position R read after its last copy, and the roots the
// master sent. 24 bytes.
internal readonly record struct LoadEndPayload(ulong Start, ulong End, ulong Roots)
{
    public const int Size = 3 * sizeof(ulong);

    public static LoadEndPayload Of(WireFrame frame)
    {
        var payload = frame.Payload;

        Assert.Equal(Size, payload.Length);

        return new LoadEndPayload(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(ulong))),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(2 * sizeof(ulong))));
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Start);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(sizeof(ulong)), End);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(2 * sizeof(ulong)), Roots);

        return bytes;
    }
}
