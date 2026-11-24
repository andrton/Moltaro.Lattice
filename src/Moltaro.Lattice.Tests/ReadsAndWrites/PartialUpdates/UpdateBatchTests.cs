using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

public sealed class UpdateBatchTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mixed_batch_checks_final_unique_keys_and_commits_every_command_together(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await typed.InsertAsync(UpdateSchema.Item("ada"));
        await typed.InsertAsync(UpdateSchema.Item("grace"));
        Assert.True(typed.TryGet("ada", out _, out var revision));

        var refused = typed.CreateUpdate("ada");
        var rejected = refused.Assign("Login", "GRACE");
        var error = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => refused.CommitAsync());
        Assert.Equal("ByLogin", error.IndexName);
        Assert.Throws<InvalidOperationException>(() => rejected.Changed);

        var update = byClass ? typed.CreateUpdate("ada") : records.CreateUpdate("ada");
        var login = update.Assign("Login", "GRACE");
        var missingTag = update.SetRemove("Tags", "other");
        var addedTag = update.SetAdd("Tags", "friend");
        var equalTag = update.SetAdd("Tags", "FRIEND");
        var grace = typed.Get("grace")!;
        grace.Login = "ADA";
        var batch = database.CreateWriteBatch();
        batch.Update(update, revision);
        batch.Replace(grace);
        Assert.Throws<InvalidOperationException>(() => login.Changed);
        Assert.Throws<InvalidOperationException>(() => missingTag.Changed);
        Assert.Throws<InvalidOperationException>(() => addedTag.Changed);
        Assert.Throws<InvalidOperationException>(() => equalTag.Changed);
        var result = await batch.CommitAsync();

        Assert.True(result.Changed);
        Assert.Equal(result.Position, database.Position);
        Assert.Equal(new[] { true, false, true, false }, new[] { login.Changed, missingTag.Changed, addedTag.Changed, equalTag.Changed });
        Assert.Equal("ada", Assert.Single(typed.Index("ByLogin").Find(IndexKey.Equal("grace"))).Id);
        Assert.Equal("grace", Assert.Single(records.Index("ByLogin").Find(IndexKey.Equal("ada")))["Id"]);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_refusal_reports_its_original_batch_index_and_keeps_every_result_unavailable(bool stale)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await items.InsertAsync(UpdateSchema.Item("ada"));
        await items.InsertAsync(UpdateSchema.Item("grace"));
        Assert.True(items.TryGet("ada", out _, out var revision));
        var before = database.Position;

        var first = items.CreateUpdate("ada");
        var changed = first.Assign("Login", "new login");
        var unchanged = first.SetAdd("Tags", "MEMBER");
        var last = records.CreateUpdate("grace");
        var failed = last.ListRemove("Scores", 9);
        var batch = database.CreateWriteBatch();
        batch.Update(first, revision);
        batch.Delete("Item", "absent");
        batch.Insert(UpdateSchema.Item("later"));
        if (stale)
        {
            batch.Update(last, revision);
        }
        else
        {
            batch.Update(last);
        }

        var error = stale ? LatticeErrorEnum.RevisionConflict : LatticeErrorEnum.UpdateTargetMissing;
        var refusal = await TestData.ExpectErrorAsync(error, () => batch.CommitAsync());
        Assert.Equal(3, refusal.OperationIndex);
        Assert.Equal(before, database.Position);
        Assert.True(items.TryGet("ada", out var stored, out var after));
        Assert.Equal(revision, after);
        Assert.Equal("ada", stored.Login);
        Assert.Equal("Member", Assert.Single(stored.Tags!));
        Assert.Null(items.Get("later"));
        Assert.Empty(items.Index("ByLogin").Find(IndexKey.Equal("new login")));
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
        Assert.Throws<InvalidOperationException>(() => unchanged.Changed);
        Assert.Throws<InvalidOperationException>(() => failed.Changed);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Missing_entity_with_a_revision_conflicts_and_a_canceled_update_cannot_be_resubmitted()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));
        Assert.True(items.TryGet("ada", out _, out var revision));
        await items.DeleteAsync("ada");
        var before = database.Position;

        var missing = items.CreateUpdate("ada");
        var gone = missing.Assign("Login", "changed");
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => missing.CommitAsync(revision));
        Assert.Throws<InvalidOperationException>(() => gone.Changed);

        var canceled = items.CreateUpdate("ada");
        var pending = canceled.Assign("Login", "changed");
        var commit = canceled.CommitAsync(new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await commit);
        Assert.Throws<InvalidOperationException>(() => pending.Changed);
        Assert.Throws<InvalidOperationException>(() => { _ = canceled.CommitAsync(); });
        Assert.Equal(before, database.Position);
    }
}
