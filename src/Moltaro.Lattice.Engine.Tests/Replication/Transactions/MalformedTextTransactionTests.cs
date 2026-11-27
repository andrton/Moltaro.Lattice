using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// The root of a Write in a transaction carries the text of a Dictionary field in the form a load carries it
// (brief of Epic 1, sprint S13, the planner's decision after the study, point 8: the child checks that form,
// for a copy of a load and an effect of the stream alike), and a transaction whose Write carries it in
// another form is malformed (MarkedTextRoot.Damages): it is refused whole, the child ends its connection
// and stays Ready at the position it had, and it holds what it loaded and nothing else — no root entry and
// no entry of a text of the refused Write, which the arena would show (MalformedTransactionTests).
//
// The master is real and holds one root the test can find (MarkedTextRoot); the child loads it and is
// Ready. A relay (FrameRelay) damages the first transaction the master sends after that, the Write of the
// record "second", with a valid check; the master then writes once more, so that a frame follows the
// damaged one.
public sealed class MalformedTextTransactionTests
{
    // In a Write, the length of the root follows the kind of the effect (TransactionPayload).
    private const int RootLengthAt = 1;

    public static IEnumerable<TheoryDataRow<string>> Damages => MarkedTextRoot.Damages;

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Transaction_whose_Write_carries_a_Dictionary_text_in_another_form_is_refused_whole_and_leaves_nothing(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedTextRoot.Schema);
        var records = master.Set("Regional");
        var entityType = records.EntityType;
        await records.InsertAsync(MarkedTextRoot.RecordOf(entityType, "code"));
        var transactions = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || ++transactions > 1)
            {
                return [frame];
            }

            var effects = TransactionPayload.EffectsOf(frame);

            Assert.Equal(TransactionPayload.Write, effects[0]);

            return [frame.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(frame), TransactionPayload.CountOf(frame), MarkedTextRoot.Damaged(effects, damage, RootLengthAt)))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;

        child.DrainRetired();
        var loaded = child.Counters.Arena;

        await records.InsertAsync(MarkedTextRoot.RecordOf(entityType, "second", MarkedTextRoot.RegionFor(damage)));
        await records.InsertAsync(MarkedTextRoot.RecordOf(entityType, "after"));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);

        var onChild = child.Set("Regional");

        Assert.Equal(MarkedTextRoot.RegionText, onChild.Get("code")?["Region"]);
        Assert.Null(onChild.Get("second"));
        Assert.Null(onChild.Get("after"));

        child.DrainRetired();
        var counters = child.Counters;

        Assert.Equal(1, counters.RootEntries);
        Assert.Equal((loaded.LiveBytes, loaded.ClassSlackBytes, 0L, 0L), (counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes, counters.Arena.PreparedBytes, counters.Arena.RetiredBytes));
    }
}
