using Moltaro.Lattice.Engine.Tests.Engine.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The root of a write a child forwards carries the text of a Dictionary field in the form a load carries
// it, and its master checks that form as a child checks a root it receives (brief of Epic 1, sprint S13,
// the planner's decision after the study, point 8: for a copy of a load, an effect of the stream and a
// forwarded write on the master alike; brief of S12, decided after the study, point 1). A request whose
// root carries it in another form (MarkedTextRoot.Damages) is malformed: the master executes nothing of it
// and ends the connection without a reply (MalformedRequestTests); once it holds nothing for the child any
// more, "code" has the revision it had, and the arena of the master holds what it held, so no entry of a
// text of the request was left.
//
// The child is made by hand (LoadedPeer) and has loaded a master of MarkedTextRoot that holds "code" and
// "filler"; the wait for "nothing is registered any more" upserts "filler" again, a root of the same size.
public sealed class MalformedTextRequestTests
{
    public static IEnumerable<TheoryDataRow<string>> Damages => MarkedTextRoot.Damages;

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Request_whose_root_carries_a_Dictionary_text_in_another_form_ends_the_connection_and_is_never_executed(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedTextRoot.Schema);
        var texts = master.Set("Regional");
        await texts.InsertAsync(MarkedTextRoot.RecordOf(texts.EntityType, "code", MarkedTextRoot.RegionFor(damage)));
        Assert.True(texts.TryGet("code", out _, out var revision));

        using var child = await LoadedPeer.ConnectAsync(master);
        await UpsertFillerAsync(master);
        master.DrainRetired();
        var before = master.Counters.Arena;

        var root = MarkedTextRoot.Damaged(child.Root.ToArray(), damage, rootLengthAt: -1);

        await child.SendRequestAsync(1, ForwardPayload.Request(ForwardPayload.Upsert, MarkedTextRoot.TypeId, ForwardPayload.KeyOf("code"), root));

        Assert.Null(await child.ReceiveReplyOrTheEndAsync(1));
        await NothingRegistered.ExpectAsync(master, () => UpsertFillerAsync(master));

        Assert.True(texts.TryGet("code", out var code, out var after));
        Assert.Equal(revision, after);
        Assert.Equal(MarkedTextRoot.RegionFor(damage), code["Region"]);

        master.DrainRetired();
        var counters = master.Counters.Arena;

        Assert.Equal((before.LiveBytes, before.ClassSlackBytes, 0L), (counters.LiveBytes, counters.ClassSlackBytes, counters.PreparedBytes));
    }

    private static async Task UpsertFillerAsync(EngineDatabase master) =>
        await master.Set("Regional").UpsertAsync(MarkedTextRoot.RecordOf(master.Set("Regional").EntityType, "filler"));
}
