namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// On a master CreateWriteBatch returns the write batch of the core: building it works, through
// classes and by name, and checks every operation when it is added, and its commit is
// NotSupportedException until Epic 1, sprint S17 (brief of S06, "Sessions and the write batch").
public sealed class WriteBatchBuildingTests
{
    [Fact]
    public async Task Write_batch_is_built_on_a_master_and_its_commit_is_not_implemented_yet()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var settings = database.Set("Setting");
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Replace(new Tag { Name = "urgent", Color = "red" });
        batch.Upsert(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });
        batch.Delete<User>(2);
        batch.Delete("Setting", "language");

        // An operation is checked when it is added, and a refused one leaves the batch as it was.
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Insert(new User { Id = 3, Name = null! }));
        Assert.Throws<ArgumentException>(() => batch.Delete<User>(1));
        Assert.Equal(5, batch.Count);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await batch.CommitAsync());

        var withSession = database.CreateWriteBatch();
        withSession.Upsert(new User { Id = 1, Name = "Ada" });
        await Assert.ThrowsAsync<NotSupportedException>(async () => await withSession.CommitAsync(new LatticeSession()));

        // Nothing was written.
        Assert.Equal(before, database.Position);
        Assert.Empty(database.Set<User>().Enumerate());
        Assert.Empty(settings.Enumerate());
    }
}
