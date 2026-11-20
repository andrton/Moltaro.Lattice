using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Opening;

// The open timeout of a child bounds a wait, not the load (brief of Epic 1, sprint S10, the planner's
// decision of 17 November 2026 after the study, point 4): how long a child waits to reach its master and
// be accepted by it, and afterwards for the next part of the database to arrive.
// - A child whose master cannot be reached keeps trying and fails with Unavailable when OpenTimeout
//   passes, and not before; it is closed and holds nothing (the allocator of the test counts its native
//   memory, CountingNativeAllocator).
// - A child whose master accepts its connection and then sends nothing fails in the same way.
// - A load that takes longer than OpenTimeout in total, while its parts keep arriving, becomes ready: a
//   link lets what the master sends through one portion at a time (HeldLink.Stepped), and the test lets
//   the next portion through a step after the last, until a step more than the timeout has passed since
//   the open began; the child is still Loading then, and the test lets the rest through. A portion holds
//   two frames of the largest body, so each wait of the child for its next frame lasts about one step.
//   The step is a tenth of the timeout of that child (ReplicationNodes.ShortOpenTimeout): whatever a
//   loaded machine adds to a step stays within the rest, and how fast the machine runs changes neither
//   the steps nor that the load outlasts the timeout.
// - A load whose next part does not arrive within OpenTimeout fails in the same way as an open that
//   receives nothing: the link lets two portions through while the child is Loading and then holds the
//   rest; it is closed and holds nothing. Its timeout is the same, so that the child stays Loading long
//   enough for the test to see it however busy the machine is; the test takes about one timeout.
// - A child started before its master becomes ready when the master opens within its timeout: the port
//   is held while the child tries (ClosedPort) and freed for the master.
// What a timeout of the master bounds is stated by SilentPeerTests.
public sealed class OpenTimeoutTests
{
    private const int Portion = 2 * 64 * 1024;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(1);

    // A timer of the system may fire a tick early.
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    // The open timeout of a child that loads through a stepped link, and the step after which the test
    // lets the next part of its load through.
    private static readonly TimeSpan LoadOpenTimeout = ReplicationNodes.ShortOpenTimeout;

    private static readonly TimeSpan Step = LoadOpenTimeout / 10;

    [Fact]
    public async Task Child_whose_master_cannot_be_reached_fails_with_Unavailable_when_its_OpenTimeout_passes_and_holds_nothing()
    {
        using var port = new ClosedPort();
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(port.Address, OpenTimeout), new EngineTestInputs { Allocator = allocator });
        var waited = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.True(waited.Elapsed >= OpenTimeout - Tolerance, $"The open failed after {waited.Elapsed}, before its timeout of {OpenTimeout}.");
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Child_whose_master_accepts_it_and_then_sends_nothing_fails_with_Unavailable_when_its_OpenTimeout_passes_and_holds_nothing()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}");
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(address, OpenTimeout), new EngineTestInputs { Allocator = allocator });
        var waited = Stopwatch.StartNew();

        var opening = child.OpenAsync().AsTask();
        using var accepted = await silent.AcceptSocketAsync().WaitAsync(Bound);
        var exception = await Assert.ThrowsAsync<LatticeException>(() => opening.WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.True(waited.Elapsed >= OpenTimeout - Tolerance, $"The open failed after {waited.Elapsed}, before its timeout of {OpenTimeout}.");
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Load_that_takes_longer_than_the_OpenTimeout_while_its_parts_keep_arriving_becomes_ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 1024 * 1024);
        await using var link = HeldLink.Stepped(master.ListenUri!, Portion);
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, LoadOpenTimeout));
        var waited = Stopwatch.StartNew();
        var opening = child.OpenAsync().AsTask();

        // The load of 2 MiB, with its frames and LoadEnd, takes more than sixteen portions, and the steps
        // until a step past the timeout are twelve at most: the link holds before each of them and still
        // holds LoadEnd after the last, so the child cannot be Ready. It received its HelloReply in the
        // first portion; still Connecting or Closed, it would have failed its open.
        while (waited.Elapsed <= LoadOpenTimeout + Step)
        {
            await link.HeldAsync();
            await Task.Delay(Step);
            link.PassNext();
        }

        Assert.Equal(LatticeDatabaseStateEnum.Loading, child.State);

        link.Release();
        await opening.WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Pieces.ExpectSame(master, child);
    }

    [Fact]
    public async Task Load_whose_next_part_does_not_arrive_within_the_OpenTimeout_fails_with_Unavailable_and_holds_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 1024 * 1024);
        await using var link = HeldLink.Stepped(master.ListenUri!, Portion);
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, LoadOpenTimeout), new EngineTestInputs { Allocator = allocator });
        var opening = child.OpenAsync().AsTask();

        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        // The child cannot receive a frame of the second portion before the test lets it through, so its
        // last wait begins after this.
        var waited = Stopwatch.StartNew();
        link.PassNext();
        await link.HeldAsync();

        var exception = await Assert.ThrowsAsync<LatticeException>(() => opening.WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.True(waited.Elapsed >= LoadOpenTimeout - Tolerance, $"The open failed {waited.Elapsed} after the last part of the load, before its timeout of {LoadOpenTimeout}.");
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Child_started_before_its_master_becomes_ready_when_the_master_opens_within_its_OpenTimeout()
    {
        using var port = new ClosedPort();
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(port.Address));

        var opening = child.OpenAsync().AsTask();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Connecting);

        // The port is freed for the master, which opens while the child keeps trying.
        port.Dispose();
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, port.Address);

        await opening.WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(master.Position, child.Position);
    }
}
