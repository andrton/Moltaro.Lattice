using Moltaro.Lattice.TestSupport.PartialUpdates;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

// The commands apply in order to the state found at execution, and only completion of the
// whole write makes their outcomes available (mutation-commands.md, "Collection operation outcomes").
public sealed class UpdateExecutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_command_sees_prior_edits_and_the_current_entity_and_reports_its_outcome(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        var item = UpdateSchema.Item("ada");
        await typed.InsertAsync(item);

        var update = byClass ? typed.CreateUpdate("ADA") : records.CreateUpdate("ADA");
        Assert.Same(typed.EntityType, update.EntityType);
        Assert.Equal(0, update.Count);
        var changedCity = update.Assign("Home.City", "Paris");
        var replaced = update.ListReplace("Scores", 1, 25);
        var inserted = update.ListInsert("Scores", 3, 30);
        var removed = update.ListRemove("Scores", 0);
        var appended = update.ListAppend("Scores", (short)35);
        var mapSet = byClass
            ? update.MapSet<string, UpdateHome?>("Options", "THEME", null)
            : update.MapSet<string, LatticeOwnedValue?>("Options", "THEME", null);
        var missingKey = update.MapRemove("Options", "absent");
        var duplicate = update.SetAdd("Tags", "MEMBER");
        var added = update.SetAdd("Tags", "friend");
        var removedMember = update.SetRemove("Tags", "FRIEND");
        var missingMember = update.SetRemove("Tags", "absent");
        Assert.Equal(11, update.Count);
        Assert.All(new[] { changedCity, replaced, inserted, removed, appended, mapSet, missingKey, duplicate, added, removedMember, missingMember },
            command => Assert.Throws<InvalidOperationException>(() => command.Changed));

        item.Scores = [40, 50, 60];
        item.Label = "written after building the update";
        await typed.ReplaceAsync(item);
        Assert.True(typed.TryGet("ada", out _, out var previousRevision));
        var session = new LatticeSession();
        var result = await update.CommitAsync(previousRevision, session);

        Assert.True(result.Changed);
        Assert.Equal(result.Position, session.MinPosition);
        Assert.Equal(new[] { true, true, true, true, true, true, false, false, true, true, false },
            new[] { changedCity.Changed, replaced.Changed, inserted.Changed, removed.Changed, appended.Changed, mapSet.Changed, missingKey.Changed, duplicate.Changed, added.Changed, removedMember.Changed, missingMember.Changed });
        Assert.True(typed.TryGet("ada", out var stored, out var revision));
        Assert.NotEqual(previousRevision, revision);
        Assert.Equal("Paris", stored.Home!.City);
        Assert.Equal(new[] { 25, 60, 30, 35 }, stored.Scores);
        Assert.Equal("written after building the update", stored.Label);
        var pair = Assert.Single(stored.Options!);
        Assert.Equal("Theme", pair.Key);
        Assert.Null(pair.Value);
        Assert.Equal("Member", Assert.Single(stored.Tags!));
        Assert.Empty(typed.Index("ByCity").Find(IndexKey.Equal("Oslo")));
        Assert.Equal("ada", Assert.Single(records.Index("ByCity").Find(IndexKey.Equal("Paris")))["Id"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_update_can_initialize_a_collection_then_edit_it_and_no_ops_keep_the_revision(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await typed.InsertAsync(new UpdateItem { Id = "ada", Login = "ada" });

        var initialize = byClass ? typed.CreateUpdate("ada") : records.CreateUpdate("ada");
        var scores = initialize.Assign("Scores", Array.Empty<int>());
        var inserted = initialize.ListInsert("Scores", 0, 10);
        LatticeCommandResult mapInserted;
        if (byClass)
        {
            initialize.Assign("Options", Array.Empty<KeyValuePair<string, UpdateHome?>>());
            mapInserted = initialize.MapSet<string, UpdateHome?>("Options", "Theme", null);
        }
        else
        {
            initialize.Assign("Options", Array.Empty<KeyValuePair<string, LatticeOwnedValue?>>());
            mapInserted = initialize.MapSet<string, LatticeOwnedValue?>("Options", "Theme", null);
        }
        var tags = initialize.Assign("Tags", new[] { "Member", "MEMBER" });
        var result = await initialize.CommitAsync();
        Assert.True(scores.Changed && inserted.Changed && mapInserted.Changed && tags.Changed);

        Assert.True(typed.TryGet("ada", out var first, out var revision));
        Assert.Equal(10, Assert.Single(first.Scores!));
        Assert.Equal("Member", Assert.Single(first.Tags!));

        var unchanged = byClass ? typed.CreateUpdate("ada") : records.CreateUpdate("ada");
        var duplicate = unchanged.SetAdd("Tags", "MEMBER");
        var absentKey = unchanged.MapRemove("Options", "absent");
        var absentMember = unchanged.SetRemove("Tags", "other");
        var noOp = await unchanged.CommitAsync(revision);

        Assert.False(noOp.Changed);
        Assert.False(duplicate.Changed || absentKey.Changed || absentMember.Changed);
        Assert.Equal(PositionSequence.Of(result.Position) + 1, PositionSequence.Of(noOp.Position));
        Assert.True(typed.TryGet("ada", out _, out var retainedRevision));
        Assert.Equal(revision, retainedRevision);

        var same = typed.CreateUpdate("ada");
        var equal = same.Assign("Login", "ada");
        var equalMapValue = same.MapSet<string, UpdateHome?>("Options", "THEME", null);
        Assert.True((await same.CommitAsync(revision)).Changed);
        Assert.True(equal.Changed && equalMapValue.Changed);
        Assert.True(typed.TryGet("ada", out _, out var nextRevision));
        Assert.NotEqual(revision, nextRevision);
    }

    [Theory]
    [InlineData("absent entity", LatticeErrorEnum.NotFound)]
    [InlineData("absent parent", LatticeErrorEnum.UpdateTargetMissing)]
    [InlineData("null collection", LatticeErrorEnum.UpdateTargetMissing)]
    [InlineData("negative position", LatticeErrorEnum.UpdateTargetMissing)]
    [InlineData("position at end", LatticeErrorEnum.UpdateTargetMissing)]
    public async Task A_missing_target_refuses_every_command_and_keeps_the_position(string target, LatticeErrorEnum error)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        item.Home = null;
        item.Options = null;
        await items.InsertAsync(item);
        Assert.True(items.TryGet("ada", out _, out var revision));
        var before = database.Position;

        var update = items.CreateUpdate(target == "absent entity" ? "unknown" : "ada");
        var first = update.Assign("Login", "changed");
        _ = target switch
        {
            "absent entity" => update.SetAdd("Tags", "new"),
            "absent parent" => update.Assign("Home.City", "Paris"),
            "null collection" => update.MapRemove("Options", "absent"),
            "negative position" => update.ListInsert("Scores", -1, 0),
            "position at end" => update.ListReplace("Scores", 2, 0),
            _ => throw new InvalidOperationException(target),
        };
        await TestData.ExpectErrorAsync(error, () => update.CommitAsync());

        Assert.Throws<InvalidOperationException>(() => first.Changed);
        Assert.Equal(before, database.Position);
        Assert.True(items.TryGet("ada", out var stored, out var after));
        Assert.Equal(revision, after);
        Assert.Equal("ada", stored.Login);
        Assert.Empty(items.Index("ByLogin").Find(IndexKey.Equal("changed")));
        Assert.Throws<InvalidOperationException>(() => update.SetAdd("Tags", "later"));
    }
}
