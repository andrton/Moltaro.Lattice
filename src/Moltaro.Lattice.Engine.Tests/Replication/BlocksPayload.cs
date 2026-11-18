using System.Buffers.Binary;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The payload of a Blocks frame of a load as the tests read it, apart from the codec of the engine
// (Replication.Load.RootFramer): the number of roots it holds, a UInt32, and for each root its length, a
// UInt64, and its bytes.
internal static class BlocksPayload
{
    // The bytes of the one small root of a Blocks frame.
    public static byte[] OnlyRootIn(WireFrame frame)
    {
        var payload = frame.Payload;

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal((ulong)(payload.Length - sizeof(uint) - sizeof(ulong)), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(uint))));

        return payload[(sizeof(uint) + sizeof(ulong))..];
    }
}
