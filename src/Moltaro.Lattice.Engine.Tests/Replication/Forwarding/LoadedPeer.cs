using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A child made by hand (Peer) that has loaded a master of MarkedRoot holding the one record "code": it
// sent the Hello of a real child (Handshake), received the whole load up to LoadEnd, and holds the root of
// "code" as a child sends it in a request (ForwardPayload.RootOf). It sends requests in the shapes of
// ForwardPayload and reads the replies of the master; it acknowledges nothing, which a master does not
// require of a child. The wait for "nothing is registered any more" on such a master writes the record
// "filler", so that the tests can state what became of "code".
internal sealed class LoadedPeer : IDisposable
{
    private LoadedPeer(Peer peer, byte[] root)
    {
        Peer = peer;
        Root = root;
    }

    public Peer Peer { get; }

    // The root of "code", its last mutation sequence 0.
    public byte[] Root { get; }

    // A master of MarkedRoot with a ListenUri that holds the record "code", and the options given.
    public static async Task<EngineDatabase> OpenMasterAsync(LatticeDatabaseOptions? options = null, EngineTestInputs? inputs = null)
    {
        var master = EngineDatabase.Create(options ?? ReplicationNodes.MasterOptions(MarkedRoot.Schema, MasterAndChild.Loopback), inputs ?? new EngineTestInputs());

        await master.OpenAsync().AsTask().WaitAsync(ReplicationNodes.Bound);
        await master.Set("Marked").InsertAsync(MarkedRoot.RecordOf(master.Set("Marked").EntityType, "code"));

        return master;
    }

    // A connect that fails closes the peer it opened before the exception leaves.
    public static async Task<LoadedPeer> ConnectAsync(EngineDatabase master)
    {
        var handshake = await Handshake.RecordAsync(master);
        var peer = await Peer.ConnectAsync(master.ListenUri!);

        try
        {
            await peer.SendAsync(handshake.Hello);

            var load = await peer.ReceiveUntilAsync(frame => frame.Kind == FrameKindEnum.LoadEnd);

            return new LoadedPeer(peer, ForwardPayload.RootOf(load.Single(frame => frame.Kind == FrameKindEnum.Blocks)));
        }
        catch
        {
            peer.Dispose();
            throw;
        }
    }

    // The body of an Upsert of "code" with its root: valid, and it changes the revision of "code".
    public byte[] UpsertOfCode() => ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), Root);

    // The body of a Delete of "code": valid, and it deletes "code".
    public static byte[] DeleteOfCode() => ForwardPayload.Request(ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), []);

    // Sends the frames of a request with the body and the id, from the next stream sequence of the peer.
    public async Task SendRequestAsync(ulong correlationId, byte[] body, ulong? announcedLength = null, int framePayload = Protocol.MaxDataPayload)
    {
        foreach (var frame in RequestFrames(correlationId, body, announcedLength, framePayload))
        {
            await Peer.SendAsync(frame);
        }
    }

    // The frames of such a request, numbered from the next stream sequence of the peer, for a test that
    // sends only some of them.
    public List<WireFrame> RequestFrames(ulong correlationId, byte[] body, ulong? announcedLength = null, int framePayload = Protocol.MaxDataPayload) =>
        ForwardPayload.Frames(ForwardPayload.WriteRequest, correlationId, Peer.NextStreamSequence, body, announcedLength, framePayload);

    // The reply to the request of the id, put together from its frames; the transactions of the stream
    // that come before and between them are passed over. A master that ends the connection first fails
    // the test.
    public async Task<ForwardPayload.Reply> ReceiveReplyAsync(ulong correlationId)
    {
        var frames = await Peer.ReceiveUntilAsync(frame => IsLastOfReply(frame, correlationId));

        return ForwardPayload.ReplyOf(ForwardPayload.BodyOf([.. frames.Where(frame => frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == correlationId)]));
    }

    // The reply to the request of the id, or null when the master ends the connection first.
    public async Task<ForwardPayload.Reply?> ReceiveReplyOrTheEndAsync(ulong correlationId)
    {
        var (frames, ended) = await Peer.ReceiveUntilOrTheEndAsync(frame => IsLastOfReply(frame, correlationId));

        return ended ? null : ForwardPayload.ReplyOf(ForwardPayload.BodyOf([.. frames.Where(frame => frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == correlationId)]));
    }

    public void Dispose() => Peer.Dispose();

    // The revision of "code" on the master, null when it holds none.
    public static LatticeRevision? RevisionOfCode(EngineDatabase master) =>
        master.Set("Marked").TryGet("code", out _, out var revision) ? revision : null;

    // Waits until the master holds nothing for a hand-made child any more (NothingRegistered), with
    // writes of the record "filler".
    public static Task ExpectNothingRegisteredAsync(EngineDatabase master) =>
        NothingRegistered.ExpectAsync(master, async () => await master.Set("Marked").UpsertAsync(MarkedRoot.RecordOf(master.Set("Marked").EntityType, "filler")));

    private static bool IsLastOfReply(WireFrame frame, ulong correlationId) =>
        frame.Kind == ForwardPayload.WriteReply && frame.CorrelationId == correlationId && frame.EndsABody;
}
