using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Faults;

// A failure injected into the publication of a write puts the instance into Faulted (brief of Epic 1,
// sprint S09, Acceptance, "Faulted", "A fault during publication and the reads that retry" and the
// answers of the planner on the fault points and after a fault; the remarks of
// LatticeDatabaseStateEnum.Faulted). A test arms a fault point of the engine (EngineDatabase.ArmFault,
// exception X3): BeforePublication, where the persistence step has completed and no store of the
// publication of the write has been made, and DuringPublication, which lies after the store of the
// root for a replace, after the store of the state for a delete and after the store of the entry of
// the index for an insert, each before the write is complete. For each point and for a replace, a
// delete and an insert:
// - The call of the write fails with IndeterminateOutcome, whose InnerException is the injected
//   InvalidOperationException, which names the point; the instance is Faulted.
// - No read returns a mixture of two states and none is left retrying. A read held inside the
//   protection (ArmReadSwitch) when the fault comes, after it found the root and before it copied it,
//   and readers that read the entity from before the fault until a read reports Unavailable, each
//   end within the bound, with the state before the write, the whole state of the write, or
//   Unavailable. For a replace the Acceptance names the two that can be: the old state or
//   Unavailable. An insert has no root that a read could be held at.
// - A read that begins after the fault, of that entity or of another, and every other operation
//   reports Unavailable.
// - The close works, and once it has completed every piece of native memory the database obtained
//   was returned (CountingNativeAllocator).
public sealed class PublicationFaultTests
{
    private const long Unavailable = ReadsUntilUnavailable.Unavailable;

    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    // The points by name: the enum of the engine is internal, and a test method is public.
    public static IEnumerable<TheoryDataRow<string, string>> Cases =>
        from point in new[] { nameof(EngineFaultPointEnum.BeforePublication), nameof(EngineFaultPointEnum.DuringPublication) }
        from kind in new[] { "replace", "delete", "insert" }
        select new TheoryDataRow<string, string>(point, kind);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Fault_in_the_publication_of_a_write_faults_the_instance_and_leaves_no_read_retrying(string point, string kind)
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(StampSchema.Schema, Arena, allocator);

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            var records = database.Set("Stamp");

            // Entity 1 is the one written, in its state 0 unless the write inserts it; entity 2 is
            // another one.
            if (kind != "insert")
            {
                await stamps.InsertAsync(StampSchema.StampOf(1, 0));
            }

            await stamps.InsertAsync(StampSchema.StampOf(2, 0));

            long?[] allowed = kind switch
            {
                "replace" => [0, Unavailable],
                "delete" => [0, null, Unavailable],
                _ => [null, 1, Unavailable],
            };

            long? Read()
            {
                try
                {
                    return StampSchema.NumberOf(1, stamps.Get(1L));
                }
                catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.Unavailable)
                {
                    return Unavailable;
                }
            }

            using var held = kind == "insert" ? null : await HeldRead.StartAsync(database, Read);
            var readers = await ReadsUntilUnavailable.StartAsync(2, Read);

            database.ArmFault(Enum.Parse<EngineFaultPointEnum>(point));

            var write = kind switch
            {
                "replace" => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)).AsTask(),
                "delete" => stamps.DeleteAsync(1L).AsTask(),
                _ => stamps.InsertAsync(StampSchema.StampOf(1, 1)).AsTask(),
            };

            var failure = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failure.Error);
            var cause = Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Contains(point, cause.Message, StringComparison.Ordinal);
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);

            if (held is not null)
            {
                Assert.Contains(await held.ReleaseAsync(), allowed);
            }

            Assert.All(await readers.EndAsync(), outcome => Assert.Contains(outcome, allowed));

            // A read that begins now, and every other operation.
            Assert.Equal(Unavailable, Read());
            ExpectUnavailable(() => stamps.Get(2L));
            ExpectUnavailable(() => records.Get(2L));
            ExpectUnavailable(() => stamps.Enumerate().ToList());
            ExpectUnavailable(() => database.Position);
            await ExpectUnavailableAsync(() => stamps.GetAsync(2L, new LatticeSession()).AsTask());
            await ExpectUnavailableAsync(() => stamps.ReplaceAsync(StampSchema.StampOf(2, 1)).AsTask());
            await ExpectUnavailableAsync(() => records.InsertAsync(StampSchema.RecordOf(records.EntityType, 3, 0)).AsTask());
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }

        Assert.Equal(0, allocator.Outstanding);
        Assert.Equal(allocator.Obtained, allocator.Returned);
    }

    private static void ExpectUnavailable<TResult>(Func<TResult> operation)
    {
        var refused = Assert.Throws<LatticeException>(() => operation());

        Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
    }

    private static async Task ExpectUnavailableAsync(Func<Task> operation)
    {
        var refused = await Assert.ThrowsAsync<LatticeException>(() => operation().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, refused.Error);
    }
}
