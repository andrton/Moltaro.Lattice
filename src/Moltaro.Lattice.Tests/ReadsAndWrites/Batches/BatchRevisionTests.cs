using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// The entities of one write batch share its one position, and each has a revision of its own (the remarks
// of LatticeRevision: "a token never matches another entity"; the engine contract, "Write coordinator and
// revision semantics": the revision binds history, type and key, and that position). So the revision of one
// member is no condition another member satisfies, in a single write or in a batch, and the revision a
// member has on a child is its revision on the master, which a write forwarded through that child is
// checked against: the child applies the batch whole, and loads a member as its master holds it.
public sealed class BatchRevisionTests
{
    [Fact]
    public async Task Entities_of_one_batch_share_its_position_and_their_revisions_do_not_satisfy_a_condition_on_each_other()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Insert(new User { Id = 2, Name = "Grace" });
        var result = await batch.CommitAsync();

        Assert.Equal(PositionSequence.Of(before) + 1, PositionSequence.Of(result.Position));
        Assert.True(users.TryGet(1L, out var ada, out var adaRevision));
        Assert.True(users.TryGet(2L, out var grace, out var graceRevision));
        Assert.NotEqual(adaRevision, graceRevision);

        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => users.ReplaceAsync(ada, graceRevision));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => users.DeleteAsync(2L, adaRevision));

        var crossed = database.CreateWriteBatch();
        crossed.Replace(grace, adaRevision);
        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => crossed.CommitAsync());

        Assert.Equal(0, refused.OperationIndex);
        Assert.Equal(result.Position, database.Position);

        // Each revision is the condition of its own entity.
        var own = database.CreateWriteBatch();
        ada.Enabled = true;
        grace.Enabled = true;
        own.Replace(ada, adaRevision);
        own.Replace(grace, graceRevision);
        await own.CommitAsync();

        Assert.True(users.Get(1L)!.Enabled);
        Assert.True(users.Get(2L)!.Enabled);
    }

    [Fact]
    public async Task Revision_of_a_member_on_a_child_is_its_revision_on_the_master_whether_the_child_followed_or_loaded_it()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var following = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();

        var batch = master.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Insert(new User { Id = 2, Name = "Grace" });
        batch.Insert(new Tag { Name = "admin", Color = "red" });
        await batch.CommitAsync(session);

        await using var loading = await TestData.OpenChildAsync(master.ListenUri!);
        Assert.True(master.Set<User>().TryGet(1L, out _, out var onMaster));
        Assert.True(master.Set<User>().TryGet(2L, out _, out var graceOnMaster));

        foreach (var child in new[] { following, loading })
        {
            var read = await child.Set<User>().GetAsync(1L, session);

            Assert.True(read.Found);
            Assert.Equal(onMaster, read.Revision);
        }

        // A write forwarded through the child is checked on the master against the revision the child read,
        // and that of the other member is no condition on it.
        var ada = (await following.Set<User>().GetAsync(1L, session)).Entity!;
        var grace = await following.Set<User>().GetAsync(2L, session);

        ada.Name = "Ada Lovelace";
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => following.Set<User>().ReplaceAsync(ada, grace.Revision));
        await following.Set<User>().ReplaceAsync(ada, onMaster);

        Assert.Equal("Ada Lovelace", master.Set<User>().Get(1L)!.Name);
        Assert.Equal(graceOnMaster, grace.Revision);
    }
}
