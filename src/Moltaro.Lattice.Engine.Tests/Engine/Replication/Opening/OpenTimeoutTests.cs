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
//   link paces what the master sends (HeldLink.Paced), a portion of two frames of the largest body and
//   then a pause of a quarter of the timeout, so the child never waits longer than half of it for the
//   next frame, and the load takes several times the timeout.
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

    private static readonly TimeSpan Pause = OpenTimeout / 4;

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
        await using var link = HeldLink.Paced(master.ListenUri!, Portion, Pause);
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, OpenTimeout));
        var waited = Stopwatch.StartNew();

        await child.OpenAsync().AsTask().WaitAsync(Bound);

        Assert.True(waited.Elapsed > OpenTimeout, $"The load took {waited.Elapsed}, not longer than the timeout of {OpenTimeout}: it does not show what it is meant to.");
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Pieces.ExpectSame(master, child);
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
