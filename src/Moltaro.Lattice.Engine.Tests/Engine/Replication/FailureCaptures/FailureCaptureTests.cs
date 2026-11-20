using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.FailureCaptures;

// The captures of the tests for a failure that a shortage of the machine may explain, each stated with a
// failure a test can cause, so that a capture cannot rot unseen: on every platform the tests run on, the
// failure keeps the code and the message of the engine and adds a line in which every part of the state
// of the system was read (TestSupport.Diagnostics.SystemState).
// - A master whose socket the system refuses (ListenFailure), with an address in use: the error of the
//   socket.
// - A write through a child that the child did not apply within its SynchronizationTimeout
//   (ApplicationWait), with the application of the child suspended: the committed position, and the
//   position the child has applied.
// - The same write through a child that waits beyond the bound (ReplicationNodes.ChildWaitingBeyondTheBound),
//   whose wait the bound the test gives it ends, with the application of the child suspended: the
//   TimeoutException of the bound, and the position the child has applied. The child cannot apply the
//   write while it is suspended, so the bound of 100 ms always ends the wait first.
public sealed class FailureCaptureTests
{
    private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Master_a_test_cannot_open_because_the_system_refused_its_socket_says_what_the_system_had_left()
    {
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        var address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)other.LocalEndpoint).Port}");

        var exception = await Assert.ThrowsAsync<LatticeException>(() => ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, address));

        Assert.Equal(LatticeErrorEnum.InvalidConfiguration, exception.Error);
        var original = Assert.IsType<LatticeException>(exception.InnerException);
        Assert.StartsWith(original.Message, exception.Message, StringComparison.Ordinal);
        Assert.Matches(@" The socket error is AddressAlreadyInUse \(native \d+\)\. ", exception.Message);
        ExpectStateOfTheSystem(exception.Message);
    }

    [Fact]
    public async Task Write_through_a_child_that_the_child_did_not_apply_in_time_says_where_the_child_is_and_what_the_system_had_left()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(
            new LatticeDatabaseOptions
            {
                Role = LatticeRoleEnum.Child,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                MasterUri = master.ListenUri,
                SynchronizationTimeout = ShortBound,
            },
            new EngineTestInputs());
        var applied = child.Position;
        using var suspension = child.SuspendApplying();

        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
        var exception = await Assert.ThrowsAsync<LatticeException>(() => ApplicationWait.WithCaptureAsync(writing.WaitAsync(ReplicationNodes.Bound), child));

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, exception.Error);
        var original = Assert.IsType<LatticeException>(exception.InnerException);
        Assert.NotNull(exception.CommittedPosition);
        Assert.Equal(original.CommittedPosition, exception.CommittedPosition);
        Assert.StartsWith(original.Message, exception.Message, StringComparison.Ordinal);
        Assert.Contains($" The child has applied position {applied}. ", exception.Message, StringComparison.Ordinal);
        ExpectStateOfTheSystem(exception.Message);
    }

    [Fact]
    public async Task Write_through_a_child_waiting_beyond_the_bound_whose_wait_the_bound_of_the_test_ends_says_where_the_child_is_and_what_the_system_had_left()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(master.ListenUri!), new EngineTestInputs());
        var applied = child.Position;
        Task<LatticeWriteResult> writing;

        using (child.SuspendApplying())
        {
            writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => ApplicationWait.WithCaptureAsync(writing.WaitAsync(ShortBound), child));

            var original = Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.StartsWith(original.Message, exception.Message, StringComparison.Ordinal);
            Assert.Contains($" The child has applied position {applied}. ", exception.Message, StringComparison.Ordinal);
            ExpectStateOfTheSystem(exception.Message);
        }

        // Once the child applies again, the write it waited for returns.
        Assert.True((await writing.WaitAsync(ReplicationNodes.Bound)).Changed);
    }

    // Every part of the state of the system was read: the limit, the handles and threads of this process
    // and the listeners of the machine are never zero while a test runs.
    private static void ExpectStateOfTheSystem(string message)
    {
        Assert.Matches(@" The state of the system after the failure: commit charge \d+ of [1-9]\d* MiB; available physical memory \d+ MiB; ", message);
        Assert.Matches(@"this process holds [1-9]\d* handles and [1-9]\d* threads; ", message);
        Assert.Matches(@"the machine has [1-9]\d* TCP listeners and \d+ TCP connections\.$", message);
    }
}
