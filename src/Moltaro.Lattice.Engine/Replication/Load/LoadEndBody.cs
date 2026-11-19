using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Load;

// The payload of LoadEnd: the capture start S, its readiness position R, read after the last root was
// copied, and the number of roots the master sent, each a UInt64, little-endian. The transactions after
// S follow LoadEnd on the same connection, and the child is Ready once it has applied R. A child whose
// count of roots differs fails its open.
internal readonly record struct LoadEndBody(ulong CaptureStart, ulong Readiness, ulong Roots)
{
    public void WriteTo(Span<byte> payload)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(payload, CaptureStart);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[sizeof(ulong)..], Readiness);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[(2 * sizeof(ulong))..], Roots);
    }

    // ValidationFailed when the payload is not Protocol.LoadEndPayloadSize bytes long, or ends before it
    // starts.
    public static LoadEndBody Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Protocol.LoadEndPayloadSize)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The payload of a LoadEnd has {Protocol.LoadEndPayloadSize} bytes, not {payload.Length}.");
        }

        var end = new LoadEndBody(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[sizeof(ulong)..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[(2 * sizeof(ulong))..]));

        if (end.Readiness < end.CaptureStart)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A LoadEnd announces the end position {end.Readiness}, before its start position {end.CaptureStart}.");
        }

        return end;
    }
}
