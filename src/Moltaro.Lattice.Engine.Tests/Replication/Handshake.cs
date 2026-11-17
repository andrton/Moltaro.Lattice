using System.Buffers;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The handshake of a real child with a real master, read from a relay between them (FrameRelay): the
// Hello the child sent, which is the Hello of a child that holds nothing yet — an empty database
// identity, history zero and an empty schema fingerprint (brief of Epic 1, sprint S10) — with the
// formats and the case table version of this process; and the HelloReply of the master, which names its
// database, its history and the fingerprint of its schema. A test that sends a Hello of its own starts
// from the child's, and takes the identity of the master from its reply where a value has to be the
// master's. The child loads and is closed before Record returns; the master sees it go a moment later.
internal sealed class Handshake
{
    private Handshake(WireFrame hello, HelloBody child, HelloBody master)
    {
        Hello = hello;
        Child = child;
        Master = master;
    }

    // The frame of the child's Hello, as the child sent it.
    public WireFrame Hello { get; }

    public HelloBody Child { get; }

    public HelloBody Master { get; }

    public static async Task<Handshake> RecordAsync(EngineDatabase master)
    {
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using (await ReplicationNodes.OpenChildAsync(relay.Address))
        {
        }

        var hello = relay.FromChild[0];
        var reply = relay.FromMaster[0];

        Assert.Equal(FrameKindEnum.Hello, hello.Kind);
        Assert.Equal(FrameKindEnum.HelloReply, reply.Kind);

        return new Handshake(hello, HelloOf(hello), HelloOf(reply));
    }

    // The frame of the child's Hello with the body given in its place.
    public WireFrame HelloWith(in HelloBody body)
    {
        var output = new ArrayBufferWriter<byte>();

        FrameCodec.WriteHelloBody(output, in body);
        return Hello.WithPayload(output.WrittenSpan);
    }

    private static HelloBody HelloOf(WireFrame frame) => FrameCodec.ReadHelloBody(new ReadOnlySequence<byte>(frame.Payload));
}
