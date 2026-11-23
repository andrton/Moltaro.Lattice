namespace Moltaro.Lattice.Tests.NestedValues.Refusals;

// A write batch does not write entities with owned values, Lists or references yet (ILatticeWriteBatch): the
// commit of a batch with such members reports NotSupportedException through its task, and nothing of the
// batch is written.
public sealed class BatchRefusalTests
{
    [Fact]
    public async Task Commit_of_a_batch_of_entities_with_owned_values_and_Lists_is_refused_through_its_task_and_writes_nothing()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var batch = database.CreateWriteBatch();

        batch.Insert(NestedSchema.RecordOf(database.Schema, NestedSchema.Full("ada")));
        batch.Insert(NestedSchema.RecordOf(database.Schema, NestedSchema.Full("grace")));

        var commit = batch.CommitAsync();

        await Assert.ThrowsAsync<NotSupportedException>(async () => await commit);

        Assert.Null(database.Set("Person").Get("ada"));
        Assert.Null(database.Set("Person").Get("grace"));
    }
}
