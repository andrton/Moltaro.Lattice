using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

public sealed class ForwardedUpdateTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Child_reports_ordered_command_results_only_after_applying_the_whole_write(bool byClass, bool inBatch)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var parent = master.Set<UpdateItem>();
        var ada = UpdateSchema.Item("ada");
        ada.Options!.Add("Retire", new UpdateHome { City = "Cairo" });
        await parent.InsertAsync(ada);
        if (inBatch)
        {
            await parent.InsertAsync(UpdateSchema.Item("linus"));
        }

        Assert.True(parent.TryGet("ada", out _, out var revision));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(master.ListenUri!), new EngineTestInputs());
        var previous = child.Position;
        var typed = child.Set<UpdateItem>();
        var named = child.Set("Item");
        var update = byClass ? typed.CreateUpdate("ada") : named.CreateUpdate("ada");
        Assert.True(child.Schema.TryGetOwnedType("Home", out var homeType));

        var city = update.Assign("Home.City", "Paris");
        var equalMember = update.SetAdd("Tags", "MEMBER");
        var inserted = update.ListInsert("Scores", 2, 7);
        var missingKey = update.MapRemove("Options", "absent");
        var replacedOption = SetOption(update, byClass, homeType, "THEME", "light", [4, 5]);
        var insertedOption = SetOption(update, byClass, homeType, "Extra", "blue", [6, 7]);
        var removedOption = update.MapRemove("Options", "RETIRE");
        var removed = update.SetRemove("Tags", "member");
        var alreadyRemoved = update.MapRemove("Options", "retire");
        List<LatticeCommandResult> outcomes =
            [city, equalMember, inserted, missingKey, replacedOption, insertedOption, removedOption, removed, alreadyRemoved];
        List<bool> expected = [true, false, true, false, true, true, true, true, false];
        var session = new LatticeSession(previous);

        Task<LatticeWriteResult> commit;
        using (child.SuspendApplying())
        {
            if (inBatch)
            {
                var other = byClass ? named.CreateUpdate("linus") : typed.CreateUpdate("linus");
                outcomes.Add(other.SetRemove("Tags", "absent"));
                outcomes.Add(other.SetAdd("Tags", "reviewer"));
                outcomes.Add(other.SetAdd("Tags", "MEMBER"));
                expected.AddRange([false, true, false]);

                var batch = child.CreateWriteBatch();
                batch.Update(update, revision);
                batch.Insert(UpdateSchema.Item("grace"));
                batch.Update(other);
                commit = batch.CommitAsync(session).AsTask();
            }
            else
            {
                commit = update.CommitAsync(revision, session).AsTask();
            }

            await Eventually.HoldsAsync(() => master.Position != previous, "The master did not publish the forwarded update.");
            Assert.False(commit.IsCompleted);
            Assert.Equal(previous, child.Position);
            Assert.Equal("Oslo", typed.Get("ada")!.Home!.City);
            Assert.Contains("Retire", typed.Get("ada")!.Options!.Keys);
            Assert.Null(typed.Get("grace"));
            if (inBatch)
            {
                Assert.Equal("Member", Assert.Single(typed.Get("linus")!.Tags!));
            }

            Assert.All(outcomes, outcome => Assert.Throws<InvalidOperationException>(() => outcome.Changed));
        }

        var result = await commit.WaitAsync(Bound);
        Assert.Equal(master.Position, result.Position);
        Assert.Equal(result.Position, child.Position);
        Assert.Equal(result.Position, session.MinPosition);
        Assert.True(result.Changed);
        Assert.Equal(expected, outcomes.Select(outcome => outcome.Changed));
        Assert.True(parent.TryGet("ada", out var masterItem, out var masterRevision));
        Assert.True(typed.TryGet("ada", out var childItem, out var childRevision));
        Assert.Equal(masterRevision, childRevision);
        Assert.NotEqual(revision, childRevision);
        Assert.Equal("Paris", childItem.Home!.City);
        Assert.Equal(masterItem.Home!.City, childItem.Home.City);
        Assert.Equal(new[] { 10, 20, 7 }, childItem.Scores);
        Assert.Equal(masterItem.Scores, childItem.Scores);
        Assert.Empty(childItem.Tags!);
        Assert.Empty(masterItem.Tags!);
        foreach (var snapshot in new[] { masterItem, childItem })
        {
            Assert.Equal(new[] { "Extra", "Theme" }, snapshot.Options!.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("light", snapshot.Options["Theme"]!.City);
            Assert.Equal(new byte[] { 4, 5 }, snapshot.Options["Theme"]!.Data);
            Assert.Equal("blue", snapshot.Options["Extra"]!.City);
            Assert.Equal(new byte[] { 6, 7 }, snapshot.Options["Extra"]!.Data);
        }

        Assert.Equal("ada", Assert.Single(named.Index("ByCity").Find(IndexKey.Equal("Paris")))["Id"]);
        Assert.Equal(inBatch, typed.Get("grace") is not null);
        if (inBatch)
        {
            var tags = new[] { "Member", "reviewer" };
            Assert.Equal(tags, typed.Get("linus")!.Tags!.Order(StringComparer.Ordinal));
            Assert.Equal(tags, parent.Get("linus")!.Tags!.Order(StringComparer.Ordinal));
        }
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Forwarded_mixed_batch_can_change_from_an_ordinary_member_when_all_command_results_are_false()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var records = child.Set("Item");
        Assert.True(records.TryGet("ada", out _, out var revision));

        var update = records.CreateUpdate("ada");
        var already = update.SetAdd("Tags", "MEMBER");
        var missing = update.MapRemove("Options", "absent");
        var batch = child.CreateWriteBatch();
        batch.Update(update);
        batch.Insert(UpdateSchema.Item("grace"));
        var result = await batch.CommitAsync().AsTask().WaitAsync(Bound);

        Assert.True(result.Changed);
        Assert.False(already.Changed);
        Assert.False(missing.Changed);
        Assert.True(records.TryGet("ada", out _, out var unchanged));
        Assert.Equal(revision, unchanged);
        Assert.NotNull(records.Get("grace"));
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task A_forwarded_batch_refusal_preserves_its_original_index_and_has_no_command_outcomes()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var previous = child.Position;
        var named = child.Set("Item");
        var succeedsAlone = named.CreateUpdate("ada");
        var first = succeedsAlone.Assign("Home.City", "Paris");
        var absent = named.CreateUpdate("absent");
        var last = absent.SetAdd("Tags", "member");
        var batch = child.CreateWriteBatch();
        batch.Update(succeedsAlone);
        batch.Insert(UpdateSchema.Item("grace"));
        batch.Update(absent);

        var refusal = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.NotFound, refusal.Error);
        Assert.Equal(2, refusal.OperationIndex);
        Assert.Throws<InvalidOperationException>(() => first.Changed);
        Assert.Throws<InvalidOperationException>(() => last.Changed);
        Assert.Equal(previous, master.Position);
        Assert.Equal(previous, child.Position);
        Assert.Null(named.Get("grace"));
        Assert.Empty(named.Index("ByCity").Find(IndexKey.Equal("Paris")));
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Timeout_after_a_known_commit_never_exposes_the_command_bits()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var previous = child.Position;
        using var suspension = child.SuspendApplying();
        var update = child.Set("Item").CreateUpdate("ada");
        var changed = update.SetAdd("Tags", "friend");
        var session = new LatticeSession { SynchronizationTimeout = TimeSpan.FromMilliseconds(100) };

        var timeout = await Assert.ThrowsAsync<LatticeException>(() => update.CommitAsync(session).AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timeout.Error);
        Assert.Equal(master.Position, timeout.CommittedPosition);
        Assert.NotEqual(previous, master.Position);
        Assert.Equal(previous, child.Position);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);

        suspension.Dispose();
        await child.WaitForPositionAsync(timeout.CommittedPosition!.Value, Bound);
        Assert.Contains("friend", child.Set<UpdateItem>().Get("ada")!.Tags!);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Close_after_receiving_the_reply_keeps_the_real_command_bits()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        await master.Set<UpdateItem>().InsertAsync(UpdateSchema.Item("ada"));
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(master.ListenUri!), new EngineTestInputs());
        var clock = ObservedClock.Of(child);
        var before = child.Position;
        var sequence = PositionSequence.Of(before) + 1;
        using var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition);
        var update = child.Set("Item").CreateUpdate("ada");
        var changed = update.Assign("Home.City", "Paris");
        var unchanged = update.SetAdd("Tags", "MEMBER");
        var commit = update.CommitAsync().AsTask();
        await held.ReachedAsync();

        // Parsing the reply precedes applying the transaction that follows it.
        Assert.False(commit.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
        var closing = child.CloseAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            var result = await commit.WaitAsync(Bound);

            Assert.Equal(before, clock.Position);
            Assert.Equal(sequence, PositionSequence.Of(result.Position));
            Assert.True(result.Changed && changed.Changed);
            Assert.False(unchanged.Changed);
        }
        finally
        {
            held.Release();
        }

        await closing.WaitAsync(Bound);
    }

    private static LatticeCommandResult SetOption(LatticeUpdate update, bool byClass, LatticeOwnedType homeType, string key, string city, byte[] data) =>
        byClass
            ? update.MapSet("Options", key, new UpdateHome { City = city, Data = data })
            : update.MapSet("Options", key, new LatticeOwnedValue(homeType) { ["City"] = city, ["Data"] = data });
}
