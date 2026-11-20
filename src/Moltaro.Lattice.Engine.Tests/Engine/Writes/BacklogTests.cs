using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// Writes submitted faster than they complete are not refused (brief of Epic 1, sprint S09, Acceptance;
// decision O20: the queue of writes has no limit of its own): 10,000 writes submitted at once all
// complete, and those to one entity in the order they were admitted. A caller alone sees its write
// complete inside the call, so the scenario test of the same sentence (WriteOrderTests) never has more
// than one write queued on the engine; here the storage step of the test (HeldCommitStorage) holds the
// first commit while the 10,000 calls are made from one thread, so that every one of them is admitted
// and queued at once: QueuedWrites counts all of them, none has completed, and once the commit is
// released all complete, the writes of each of 100 entities at increasing positions, and the last
// state of each is that of its last call.
public sealed class BacklogTests
{
    private const int Entities = 100;

    private const int Rounds = 100;

    [Fact]
    public async Task Ten_thousand_writes_queued_behind_a_held_commit_are_all_admitted_and_all_complete_in_the_order_of_their_calls()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            for (var id = 1; id <= Entities; id++)
            {
                await pieces.InsertAsync(PieceSchema.Of(id, name: 10));
            }

            storage.Hold();

            var first = pieces.InsertAsync(PieceSchema.Of(0, name: 10)).AsTask();
            await storage.NextAsync();

            var writes = new Task<LatticeWriteResult>[Rounds, Entities];

            for (var round = 0; round < Rounds; round++)
            {
                for (var entity = 0; entity < Entities; entity++)
                {
                    writes[round, entity] = pieces.ReplaceAsync(new PieceSchema.Piece { Id = entity + 1, Name = "replaced", Count = round }).AsTask();
                }
            }

            var all = writes.Cast<Task<LatticeWriteResult>>().ToArray();

            Assert.Equal((Rounds * Entities) + 1, database.Counters.QueuedWrites);
            Assert.DoesNotContain(all, write => write.IsCompleted);

            storage.ReleaseAll();
            await Task.WhenAll([first, .. all]).WaitAsync(Bound);

            for (var entity = 0; entity < Entities; entity++)
            {
                var sequences = Enumerable.Range(0, Rounds).Select(round => PositionSequence.Of(writes[round, entity].Result.Position)).ToList();

                Assert.Equal(sequences.Order(), sequences);
                Assert.Equal(Rounds, sequences.Distinct().Count());
                Assert.Equal(Rounds - 1, pieces.Get((long)entity + 1)?.Count);
            }

            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
