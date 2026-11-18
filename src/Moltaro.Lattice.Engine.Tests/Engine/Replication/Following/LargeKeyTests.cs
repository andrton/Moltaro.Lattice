namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// A String key may be as long as a String value may be (ValueBound.Bytes), so the transaction of a delete,
// which carries the key, may be longer than one block of the tail the master delivers from
// (DeliveryTail.BlockSize). A transaction of any size is delivered (brief of Epic 1, sprint S11, the
// planner's block of 15 November 2026), and a write of the master is never refused because of a child
// (decided after the study, point 5): a master with a child following deletes an entity whose key is 2 MiB
// of text, the delete succeeds, the master stays Ready, and the child no longer has the key. Review round
// 1 of S11 found the master faulted there.
public sealed class LargeKeyTests
{
    private const int KeyLength = 2 * 1024 * 1024;

    [Fact]
    public async Task Delete_of_an_entity_whose_key_is_longer_than_a_block_of_the_tail_reaches_the_child_and_the_master_stays_Ready()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(KeySchema.Of("Utf8", "Ordinal", "String"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var records = master.Set("String");
        var key = new string('k', KeyLength);

        var inserted = await records.InsertAsync(KeySchema.RecordOf(records, key, "body"));
        await Eventually.AtAsync(child, inserted.Position);
        Assert.Equal("body", child.Set("String").Get(key)?["Body"]);

        var deleted = await records.DeleteAsync(key);

        Assert.True(deleted.Changed);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, master.State);

        await Eventually.AtAsync(child, deleted.Position);
        Assert.Null(child.Set("String").Get(key));
        Assert.Empty(child.Set("String").Enumerate());
    }
}
