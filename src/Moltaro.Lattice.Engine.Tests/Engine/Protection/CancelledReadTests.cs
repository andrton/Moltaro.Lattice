using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Findings;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A read by key with a session honors its token inside the wait for a slot of the epoch table, as a
// step of an enumeration does (brief of Epic 1, sprint S09, part 2, the second of the findings of the
// outside review it carries; exception X6: the wait for a slot has the token wherever the caller gave
// one; the remarks of ILatticeSet: a cancellation takes effect while an operation is waiting). Every
// slot is held (FullEpochTable), and a GetAsync of a stamp that exists, with a session that depends on
// no position, waits for a slot; its token is cancelled after 500 ms. It ends in
// OperationCanceledException while every slot is still held, through the class and by name. The
// synchronous reads have no token; they wait for a slot as FullEpochTableTests states.
public sealed class CancelledReadTests
{
    private const int Keys = 4;

    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory(Skip = OpenFindings.OutsideReview)]
    [InlineData("by class")]
    [InlineData("by name")]
    public async Task Read_with_a_session_cancelled_while_every_slot_of_the_epoch_table_is_held_ends_in_OperationCanceledException_while_the_slots_are_still_held(string form)
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            var records = database.Set("Stamp");

            for (var key = 0; key < Keys; key++)
            {
                await stamps.InsertAsync(StampSchema.StampOf(key, key));
            }

            using var cancellation = new CancellationTokenSource();
            using var full = await FullEpochTable.StartAsync(database, 0);

            await full.ExpectCancelledWhileHeldAsync(
                () => form == "by class"
                    ? StampSchema.NumberOf(1, stamps.GetAsync(1L, new LatticeSession(), cancellation.Token).AsTask().GetAwaiter().GetResult().Entity)
                    : StampSchema.NumberOf(1, records.GetAsync(1L, new LatticeSession(), cancellation.Token).AsTask().GetAwaiter().GetResult().Entity),
                cancellation,
                Wait);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
