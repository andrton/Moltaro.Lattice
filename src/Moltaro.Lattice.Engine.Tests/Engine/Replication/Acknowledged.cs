using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// The sequences a child acknowledged to its master through a relay, in the order it sent them.
internal static class Acknowledged
{
    public static ulong[] By(FrameRelay relay) =>
        [.. relay.FromChild.Where(frame => frame.Kind == FrameKindEnum.Applied)
            .Select(frame => BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload))];
}
