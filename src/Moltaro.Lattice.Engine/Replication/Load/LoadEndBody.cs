using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// The payload of LoadEnd (the planner's decision of 17 November 2026 after the study of S10, point 3):
// the sequence at which the load ended on the master and the number of roots it sent, each a UInt64,
// little-endian. A child whose count of roots differs fails its open.
internal readonly record struct LoadEndBody(ulong Sequence, ulong Roots)
{
    public void WriteTo(Span<byte> payload)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(payload, Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[sizeof(ulong)..], Roots);
    }

    // ValidationFailed when the payload is not Protocol.LoadEndPayloadSize bytes long.
    public static LoadEndBody Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Protocol.LoadEndPayloadSize)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The payload of a LoadEnd has {Protocol.LoadEndPayloadSize} bytes, not {payload.Length}.");
        }

        return new LoadEndBody(BinaryPrimitives.ReadUInt64LittleEndian(payload), BinaryPrimitives.ReadUInt64LittleEndian(payload[sizeof(ulong)..]));
    }
}
