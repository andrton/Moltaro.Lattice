using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a master does with the length a request announces in its first frame (brief of Epic 1, sprint S12,
// decided after the study, point 13), stated with a child made by hand (LoadedPeer):
// - A length above the MemoryLimit of the master is refused at the first frame, with WriteRejected and
//   Limit Memory, before the rest arrives; the master reads and drops the rest of that request and keeps
//   the connection, so the next request is executed.
// - A request whose frames hold more than it announced is malformed: the master ends the connection and
//   executes nothing of it.
// - For every other request the master's buffer grows with the bytes received, never by the announced
//   length: a first frame of 64 KiB that announces 1 GiB, under a limit of 2 GiB, followed by the end of
//   the connection, makes the process allocate far less than the announced length. The managed memory
//   is counted over the whole process (GC.GetTotalAllocatedBytes), so the test runs alone
//   (WholeProcessAllocationCollection).
[Collection<WholeProcessAllocationCollection>]
public sealed class AnnouncedLengthTests
{
    private const long Mebibyte = 1024 * 1024;

    [Fact]
    public async Task Request_announced_above_the_memory_limit_of_the_master_is_refused_at_its_first_frame_and_the_next_request_is_executed()
    {
        var options = ReplicationNodes.MasterOptions(MarkedRoot.Schema, MasterAndChild.Loopback);
        await using var master = await LoadedPeer.OpenMasterAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = options.PersistenceMode,
            Schema = options.Schema,
            ListenUri = options.ListenUri,
            MemoryLimit = 8 * Mebibyte,
        });
        using var child = await LoadedPeer.ConnectAsync(master);
        var tooLarge = child.RequestFrames(1, new byte[(8 * Mebibyte) + 1]);

        await child.Peer.SendAsync(tooLarge[0]);
        var refused = await child.ReceiveReplyAsync(1);

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Refusal?.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Refusal?.Limit);

        foreach (var frame in tooLarge.Skip(1))
        {
            await child.Peer.SendAsync(frame);
        }

        await child.SendRequestAsync(2, LoadedPeer.DeleteOfCode());
        var deleted = await child.ReceiveReplyAsync(2);

        Assert.Null(deleted.Refusal);
        Assert.True(deleted.Changed);
        Assert.Null(LoadedPeer.RevisionOfCode(master));
    }

    [Fact]
    public async Task Request_whose_frames_hold_more_than_it_announced_ends_the_connection_and_is_not_executed()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);
        var body = LoadedPeer.DeleteOfCode();

        await child.SendRequestAsync(1, body, announcedLength: (ulong)body.Length - 1);

        Assert.Null(await child.ReceiveReplyOrTheEndAsync(1));

        await LoadedPeer.ExpectNothingRegisteredAsync(master);
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Fact]
    public async Task Master_takes_no_memory_for_what_a_request_announces_before_its_bytes_arrive()
    {
        const long Announced = 1024 * Mebibyte;
        var options = ReplicationNodes.MasterOptions(MarkedRoot.Schema, MasterAndChild.Loopback);
        await using var master = await LoadedPeer.OpenMasterAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = options.PersistenceMode,
            Schema = options.Schema,
            ListenUri = options.ListenUri,
            MemoryLimit = 2 * Announced,
        });
        using var child = await LoadedPeer.ConnectAsync(master);
        var first = child.RequestFrames(1, new byte[Protocol.MaxDataPayload - sizeof(ulong)], announcedLength: Announced)[0];
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await child.Peer.SendAsync(first.WithFlags(0));
        child.Dispose();
        await LoadedPeer.ExpectNothingRegisteredAsync(master);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(allocated < 64 * Mebibyte, $"The process allocated {allocated} bytes for a request that announced {Announced} and sent {Protocol.MaxDataPayload}.");
    }
}
