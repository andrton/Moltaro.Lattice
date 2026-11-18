using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Skips;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A session belongs to one database history (LatticeSession), and the first operation with a session
// binds it to the history of its database. A write binds it when it is admitted, before it is executed,
// so that the check of the history comes before the state-dependent execution (decision on session
// consistency, docs/decisions/session-consistency.md, points 4 and 7): a write with a session of another
// history, or one whose session another database bound first, is refused with HistoryMismatch and
// changes nothing; a write that committed returns its result, and the session is never a reason to
// replace that result with a failure. The storage step of the first database holds its commit
// (HeldCommitStorage), so that its write is admitted and executed while a write on another database
// takes the same empty session.
public sealed class SessionHistoryTests
{
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public async Task Write_admitted_with_an_empty_session_binds_it_so_that_a_write_on_another_database_with_it_is_refused_and_the_first_returns_its_result()
    {
        var storage = new HeldCommitStorage();
        await using var first = await EngineMaster.OpenAsync(StampSchema.Schema, new EngineTestInputs { CommitStorage = storage });
        await using var other = await EngineMaster.OpenAsync(StampSchema.Schema);
        var session = new LatticeSession();

        try
        {
            // Admitted and executed; its commit waits in the storage step.
            var committing = first.Set<StampSchema.Stamp>().InsertAsync(StampSchema.StampOf(1, 1), session).AsTask();
            var commit = await storage.NextAsync();

            var refused = await Assert.ThrowsAsync<LatticeException>(async () => await other.Set<StampSchema.Stamp>().InsertAsync(StampSchema.StampOf(2, 1), session));

            Assert.Equal(LatticeErrorEnum.HistoryMismatch, refused.Error);
            Assert.Null(other.Set<StampSchema.Stamp>().Get(2L));

            commit.Release();
            var written = await committing.WaitAsync(TimeSpan.FromMinutes(1));

            Assert.Equal(written.Position, session.MinPosition);
            Assert.NotNull(first.Set<StampSchema.Stamp>().Get(1L));
        }
        finally
        {
            storage.ReleaseAll();
        }
    }
}
