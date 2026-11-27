using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Child;

// A ready child that cannot hold the next change pauses at it, keeps its reads at its position and
// waits for memory to be freed, without waiting for a retry pause: the pause is set far beyond the
// bound of every wait of these tests. A free made at the pressure point, before the child registers
// its wait, is seen by its second attempt, which applies the change; a free made while it waits
// wakes it. Its own rollback does not wake it, and close ends the wait. Applied once and in order is
// shown by the position, the revisions and the acknowledgements.
public sealed class ChildMemoryWaitTests
{
    private const long ChildLimit = 16L * Segment;
    private static readonly TimeSpan FarPause = TimeSpan.FromMinutes(10);

    [Fact(Skip = Pending)]
    public async Task Memory_freed_between_the_refusal_and_the_registration_of_the_wait_resumes_the_child_at_once()
    {
        await using var fixture = await PausingChild.OpenAsync(releaseAtPause: true);
        var change = await fixture.ChangeAsync();
        await fixture.Child.WaitForPositionAsync(change.Position, Bound);

        var pressure = await fixture.Pressure.WaitAsync(Bound);
        Assert.Equal(PositionSequence.Of(change.Position), pressure.Sequence);
        Assert.Equal(LatticeLimitEnum.Memory, pressure.Limit);
        Assert.False(pressure.DuringReceive);
        Assert.Equal(2, fixture.Attempts);
        fixture.ExpectSameAsMaster();
    }

    [Fact(Skip = Pending)]
    public async Task A_paused_child_keeps_its_reads_and_resumes_in_order_once_when_memory_is_freed()
    {
        await using var fixture = await PausingChild.OpenAsync(releaseAtPause: false);
        var start = fixture.Child.Position;
        var change = await fixture.ChangeAsync();
        var later = await fixture.Master.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = 3, Number = 30 });
        var pressure = await fixture.Pressure.WaitAsync(Bound);
        Assert.Equal(PositionSequence.Of(change.Position), pressure.Sequence);

        var local = fixture.Child.Set<IndexSchema.Counted>();
        Assert.Equal(start, fixture.Child.Position);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, fixture.Child.State);
        Assert.Equal(7L, local.Get(1L)?.Number);
        Assert.Single(local.Index("ByNumber").Find(IndexKey.Equal(7L)));
        Assert.Empty(local.Index("ByNumber").Find(IndexKey.Equal(8L)));
        Assert.Null(local.Get(2L));
        Assert.Null(local.Get(3L));
        Assert.DoesNotContain(Acknowledged.By(fixture.Relay), sequence => sequence >= pressure.Sequence);

        fixture.Release();
        await fixture.Child.WaitForPositionAsync(later.Position, Bound);
        fixture.ExpectSameAsMaster();
        Assert.Equal(30L, local.Get(3L)?.Number);
        var acknowledged = Acknowledged.By(fixture.Relay);
        Assert.Equal(acknowledged.Order(), acknowledged);
        await Eventually.HoldsAsync(() => Acknowledged.By(fixture.Relay).Contains(PositionSequence.Of(later.Position)),
            "The child did not acknowledge the changes it resumed.");
    }

    [Fact(Skip = Pending)]
    public async Task Close_ends_the_wait_of_a_paused_child()
    {
        var fixture = await PausingChild.OpenAsync(releaseAtPause: false);
        try
        {
            var start = fixture.Child.Position;
            _ = await fixture.ChangeAsync();
            _ = await fixture.Pressure.WaitAsync(Bound);
            Assert.Equal(start, fixture.Child.Position);

            // The attempt after the registration is refused too; its rollback wakes nothing, so no
            // third attempt comes while the retry pause lasts.
            await Eventually.HoldsAsync(() => fixture.Attempts == 2, "The child did not attempt the change again after its pause began.");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.Equal(2, fixture.Attempts);

            await fixture.Child.CloseAsync().AsTask().WaitAsync(Bound);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, fixture.Child.State);
            fixture.Allocator.ExpectEverythingReturned();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // A master with one Counted row, and a child of it through a relay that pauses at the first
    // member of the next change: before that member is prepared the test holds all of the child's
    // memory but 64 bytes, and frees it at the pause or when the test releases it.
    private sealed class PausingChild : IAsyncDisposable
    {
        private readonly TaskCompletionSource<ChildPressureSample> _pressure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _releaseAtPause;
        private int _armed;
        private int _attempts;
        private IDisposable? _held;

        public EngineDatabase Master { get; private set; } = null!;

        public FrameRelay Relay { get; private set; } = null!;

        public EngineDatabase Child { get; private set; } = null!;

        public CountingNativeAllocator Allocator { get; } = new();

        public Task<ChildPressureSample> Pressure => _pressure.Task;

        public int Attempts => Volatile.Read(ref _attempts);

        public static async Task<PausingChild> OpenAsync(bool releaseAtPause)
        {
            var fixture = new PausingChild { _releaseAtPause = releaseAtPause };
            fixture.Master = await InstanceBudget.OpenAsync(64L * Segment, new CountingNativeAllocator(), listen: true, schema: IndexSchema.Utf8);
            var seeded = await fixture.Master.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = 1, Number = 7 });
            fixture.Relay = FrameRelay.Start(fixture.Master.ListenUri!);
            fixture.Child = await ReplicationNodes.OpenChildAsync(
                ReplicationNodes.ChildOptions(fixture.Relay.Address, ReplicationNodes.BeyondTheBound, ChildLimit), new EngineTestInputs
                {
                    Allocator = fixture.Allocator,
                    DisableBackgroundMaintenance = true,
                    ChildRetryPause = FarPause,
                    BeforeChildPreparationMember = fixture.BeforeMember,
                    ObserveChildPressure = fixture.AtPause,
                });
            await fixture.Child.WaitForPositionAsync(seeded.Position, Bound);
            return fixture;
        }

        // The change that pauses the child: an update of the seeded row and an insert of another.
        public Task<LatticeWriteResult> ChangeAsync()
        {
            Volatile.Write(ref _armed, 1);
            var batch = Master.CreateWriteBatch();
            batch.Replace(new IndexSchema.Counted { Id = 1, Number = 8 });
            batch.Insert(new IndexSchema.Counted { Id = 2, Number = 20 });
            return batch.CommitAsync().AsTask().WaitAsync(Bound);
        }

        public void Release() => Interlocked.Exchange(ref _held, null)?.Dispose();

        public void ExpectSameAsMaster()
        {
            var source = Master.Set<IndexSchema.Counted>();
            var local = Child.Set<IndexSchema.Counted>();
            Assert.Equal(Master.Position, Child.Position);
            Assert.Equal(source.Enumerate().OrderBy(row => row.Id).Select(row => (row.Id, row.Number)),
                local.Enumerate().OrderBy(row => row.Id).Select(row => (row.Id, row.Number)));
            Assert.True(source.TryGet(1L, out _, out var masterRevision));
            Assert.True(local.TryGet(1L, out _, out var childRevision));
            Assert.Equal(masterRevision, childRevision);
            Assert.Equal(1L, Assert.Single(local.Index("ByNumber").Find(IndexKey.Equal(8L))).Id);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await Child.DisposeAsync();
            await Relay.DisposeAsync();
            await Master.DisposeAsync();
        }

        // Under the writes lock, before each attempt at a member of a received change.
        private void BeforeMember(uint member)
        {
            if (member != 1 || Volatile.Read(ref _armed) == 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _attempts) == 1)
            {
                _held = HeldInventory.LeaveAbout(Child, 64, out _);
            }
        }

        // After the rollback of the refused attempt, before the child registers its wait.
        private void AtPause(ChildPressureSample sample)
        {
            _pressure.TrySetResult(sample);
            if (_releaseAtPause)
            {
                Release();
            }
        }
    }
}
