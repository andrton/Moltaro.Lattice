using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

public sealed class ChildApplyBudgetTests
{
    private const long ChildLimit = 16L * Segment;
    private const int FirstKey = 10_000;
    private const int ExistingKey = 1;

    [Theory(Skip = Pending)]
    [InlineData(5, "insert", false)]
    [InlineData(5, "replace", true)]
    [InlineData(1_025, "insert", false)]
    [InlineData(1_025, "replace", false)]
    [InlineData(1_025, "sparse", false)]
    [InlineData(1_025, "delete", false)]
    [InlineData(1_026, "oversized", false)]
    public async Task Refusal_while_preparing_a_late_member_keeps_no_prepared_root_or_index_entry(
        int member, string effect, bool closes)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true, schema: IndexSchema.Utf8);
        var rows = master.Set<IndexSchema.Counted>();
        var readiness = await rows.InsertAsync(new IndexSchema.Counted { Id = ExistingKey, Number = 7 });
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var allocator = new CountingNativeAllocator();
        var pause = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refusals = 0;
        EngineDatabase? receiving = null;
        IDisposable? held = null;
        (int Handles, long Blocks, long Native)? prefix = null;
        var fullRoot = effect is "insert" or "replace";
        var oversized = effect == "oversized";
        Array[]? enlarged = null;
        Array[]? rebuilt = null;
        await using var child = EngineDatabase.Create(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, ChildLimit),
            new EngineTestInputs
            {
                Allocator = allocator,
                DisableBackgroundMaintenance = true,
                BeforeReceivedRootListGrowth = (count, index, _, _) =>
                {
                    if (fullRoot && index == member && held is null)
                    {
                        Assert.Equal(member - 1, count);
                        HoldRoom();
                    }
                },
                BeforeChildPreparationMember = index =>
                {
                    if (!fullRoot && index == member)
                    {
                        if (oversized)
                        {
                            var follower = Backing<Follower>(receiving!, "_follower");
                            var changes = Backing<object>(follower, "_changes");
                            var current = Backing<Array>(Backing<object>(changes, "_prepared"), "_items");
                            var names = Backing<object>(Backing<PublicationKeys>(changes, "_keys"), "_keys");
                            var keyBackings = DictionaryArrays(names);
                            Assert.True(current.Length >= 1_025);
                            Assert.Equal(2, keyBackings.Length);
                            Assert.All(keyBackings, array => Assert.True(array.Length >= 1_025));
                            Array[] actual = [current, .. keyBackings];
                            _ = ExpectCapacity(receiving!, allocator, out _, actual);
                            if (enlarged is null)
                            {
                                enlarged = actual;
                            }
                            else
                            {
                                rebuilt = actual;
                            }
                        }

                        if (held is null)
                        {
                            HoldRoom();
                        }
                    }
                },
                ObserveChildPressure = sample =>
                {
                    Interlocked.Increment(ref refusals);
                    pause.TrySetResult(sample);
                },
            });
        receiving = child;
        await child.OpenAsync().AsTask().WaitAsync(Bound);
        var local = child.Set<IndexSchema.Counted>();
        var prior = Assert.Single(local.Index("ByNumber").Find(IndexKey.Equal(7L)));
        Assert.True(local.TryGet((long)ExistingKey, out _, out var revision));
        var loaded = ExpectCapacity(child, allocator);
        Assert.True(loaded.NativeBytes > 0);

        try
        {
            var batch = master.CreateWriteBatch();
            for (var index = 0; index < member - 1; index++)
            {
                batch.Insert(new IndexSchema.Counted { Id = FirstKey + index, Number = 12 + index });
            }

            if (oversized)
            {
                batch.Insert(new IndexSchema.Ranked
                {
                    Id = 17,
                    Number = 20,
                    Region = "borrowed",
                    Login = new string('a', 6 * 1024 * 1024),
                });
            }
            else
            {
                AddLast(batch, rows.CreateUpdate((long)ExistingKey), member, effect);
            }
            var commit = await batch.CommitAsync().AsTask().WaitAsync(Bound);
            var blocked = PositionSequence.Of(commit.Position);
            var observed = await pause.Task.WaitAsync(Bound);
            Assert.Equal(blocked, observed.Sequence);
            Assert.Equal(LatticeLimitEnum.Memory, observed.Limit);
            Assert.False(observed.DuringReceive);
            var prepared = Assert.IsType<ValueTuple<int, long, long>>(prefix);
            Assert.Equal(member, prepared.Item1);
            Assert.True(prepared.Item2 > 0);
            Assert.True(prepared.Item3 >= loaded.NativeBytes);
            Assert.Equal(readiness.Position, child.Position);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Equal(1, child.Counters.RootEntries);
            Assert.Equal(child.ReservedNodeBytes(), child.Counters.Arena.PreparedBytes);
            Assert.Equal(loaded.NativeBytes, ExpectCapacity(child, allocator).NativeBytes);
            if (oversized)
            {
                var grown = Assert.IsType<Array[]>(enlarged);
                Assert.True(grown.Sum(ManagedObjectSize) > 1_024 * 96);
                _ = child.InspectBudget((_, backing) =>
                {
                    foreach (var old in grown)
                    {
                        Assert.NotSame(old, backing);
                    }
                });
            }
            Assert.True(local.TryGet((long)ExistingKey, out var installed, out var still));
            Assert.Equal(7L, installed.Number);
            Assert.Equal(revision, still);
            Assert.Equal(prior.Id, Assert.Single(local.Index("ByNumber").Find(IndexKey.Equal(7L))).Id);
            Assert.Empty(local.Index("ByNumber").Find(IndexKey.Equal(12L)));
            Assert.DoesNotContain(ChildReceiveBudgetTests.Acknowledged(relay), sequence => sequence >= blocked);
            OwnershipAudit.Expect(child);

            var next = rows.CreateUpdate((long)FirstKey);
            next.Assign("Number", 8_001L);
            var later = await next.CommitAsync().AsTask().WaitAsync(Bound);
            using var cancellation = new CancellationTokenSource();
            var waiting = child.WaitForPositionAsync(later.Position, ReplicationNodes.BeyondTheBound, cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.InRange(Volatile.Read(ref refusals), 1, 128);
            if (closes)
            {
                await child.CloseAsync().AsTask().WaitAsync(Bound);
                Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
                allocator.ExpectEverythingReturned();
                await NothingRegistered.ExpectAsync(master, async () =>
                    await rows.UpsertAsync(new IndexSchema.Counted { Id = 1_000, Number = 3 }));
                return;
            }

            held!.Dispose();
            await child.WaitForPositionAsync(later.Position, Bound);
            if (oversized)
            {
                var replacement = Assert.IsType<Array[]>(rebuilt);
                Assert.All(replacement, backing => Assert.DoesNotContain(backing, enlarged!, ReferenceEqualityComparer.Instance));
                Assert.Equal(6 * 1024 * 1024, child.Set<IndexSchema.Ranked>().Get(17L)?.Login.Length);
            }
            Assert.Equal(later.Position, child.Position);
            Assert.Equal(FirstKey, Assert.Single(local.Index("ByNumber").Find(IndexKey.Equal(8_001L))).Id);
            var expected = rows.Enumerate().OrderBy(row => row.Id).Select(row => (row.Id, row.Number));
            var actual = local.Enumerate().OrderBy(row => row.Id).Select(row => (row.Id, row.Number));
            Assert.Equal(expected, actual);
            Assert.True(rows.TryGet((long)FirstKey, out _, out var masterRevision));
            Assert.True(local.TryGet((long)FirstKey, out _, out var childRevision));
            Assert.Equal(masterRevision, childRevision);
            OwnershipAudit.Expect(child);
            ExpectCapacity(child, allocator);
        }
        finally
        {
            held?.Dispose();
        }

        void HoldRoom()
        {
            var node = receiving!;
            var counters = node.Counters;
            prefix = (counters.RootEntries, counters.Arena.PreparedBytes, allocator.OutstandingBytes);
            var remaining = ChildLimit - node.InspectBudget().ChargedBytes;
            held = node.HoldBudgetBacking(checked((int)remaining - 64));
        }
    }

    private static void AddLast(ILatticeWriteBatch batch, LatticeUpdate sparse, int member, string effect)
    {
        var row = new IndexSchema.Counted { Id = ExistingKey, Number = 1_001 };
        switch (effect)
        {
            case "insert":
                row.Id = FirstKey + member - 1;
                batch.Insert(row);
                break;
            case "replace":
                batch.Replace(row);
                break;
            case "sparse":
                sparse.Assign("Number", 1_001L);
                batch.Update(sparse);
                break;
            case "delete":
                batch.Delete<IndexSchema.Counted>((long)ExistingKey);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(effect));
        }
    }
}
