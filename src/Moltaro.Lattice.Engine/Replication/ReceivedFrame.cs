namespace Moltaro.Lattice.Replication;

// A frame a connection received (FrameConnection.ReceiveAsync): its header and its payload, the body
// without its check. The payload lies in the receive buffer of the connection and is valid only until the
// next receive on it.
internal readonly record struct ReceivedFrame(FrameHeader Header, ReadOnlyMemory<byte> Payload)
{
    public FrameKindEnum Kind => Header.Kind;

    public bool EndsABody => (Header.Flags & Protocol.EndsABody) != 0;

    public ulong CorrelationId => Header.CorrelationId;
}
