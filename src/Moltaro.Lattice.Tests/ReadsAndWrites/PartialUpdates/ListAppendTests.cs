using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

public sealed class ListAppendTests
{
    [Theory]
    [InlineData(2_000, true)]
    [InlineData(8_000, false)]
    public async Task Sequential_appends_complete_in_order_and_the_child_applies_the_same_list_and_revision(int count, bool byClass)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(UpdateSchema.Schema);
        var masterItems = master.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        item.Scores = [];
        var inserted = await masterItems.InsertAsync(item);
        Assert.True(masterItems.TryGet("ada", out _, out var initialRevision));

        await using var child = await TestDatabases.OpenChildAsync(master);
        var childItems = child.Set<UpdateItem>();
        Assert.Equal(inserted.Position, child.Position);
        Assert.True(childItems.TryGet("ada", out var predecessor, out var predecessorRevision));
        Assert.Equal(initialRevision, predecessorRevision);
        Assert.Empty(predecessor.Scores!);

        var update = byClass ? masterItems.CreateUpdate("ada") : master.Set("Item").CreateUpdate("ada");
        var expected = new List<int>(count);
        var commands = new LatticeCommandResult[count];

        for (var index = 0; index < count; index++)
        {
            var score = index % 2 == 0 ? index : -index;
            commands[index] = update.ListAppend("Scores", score);
            expected.Add(score);
        }

        Assert.All(commands, command => Assert.Throws<InvalidOperationException>(() => command.Changed));

        var committed = await update.CommitAsync(initialRevision);

        Assert.True(committed.Changed);
        Assert.All(commands, command => Assert.True(command.Changed));
        Assert.Equal(committed.Position, master.Position);
        Assert.True(masterItems.TryGet("ada", out var onMaster, out var masterRevision));
        Assert.NotEqual(initialRevision, masterRevision);
        Assert.Equal<int>(expected, onMaster.Scores);

        await child.WaitForPositionAsync(committed.Position, TestWait.Bound);

        Assert.Equal(committed.Position, child.Position);
        Assert.True(childItems.TryGet("ada", out var onChild, out var childRevision));
        Assert.Equal(masterRevision, childRevision);
        Assert.Equal<int>(expected, onChild.Scores);
    }

    [Fact]
    public async Task Appends_and_positioned_edits_follow_the_order_of_middle_and_end_changes()
    {
        await using var database = await TestDatabases.OpenMasterAsync(UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        item.Scores = [10, 20, 30, 40];
        var expected = new List<int>(item.Scores);
        await items.InsertAsync(item);

        var update = items.CreateUpdate("ada");
        var firstAppend = update.ListAppend("Scores", 50);
        expected.Add(50);

        var insertedInMiddle = update.ListInsert("Scores", 2, 25);
        expected.Insert(2, 25);
        var replacedInMiddle = update.ListReplace("Scores", 3, -30);
        expected[3] = -30;
        var removedInMiddle = update.ListRemove("Scores", 1);
        expected.RemoveAt(1);

        var removedLast = update.ListRemove("Scores", expected.Count - 1);
        expected.RemoveAt(expected.Count - 1);
        var appendedAfterRemoval = update.ListAppend("Scores", -50);
        expected.Add(-50);
        var replacedLast = update.ListReplace("Scores", expected.Count - 1, -51);
        expected[^1] = -51;

        var duplicateTag = update.SetAdd("Tags", "MEMBER");
        var insertedAtEnd = update.ListInsert("Scores", expected.Count, -60);
        expected.Add(-60);
        var lastAppend = update.ListAppend("Scores", 70);
        expected.Add(70);

        LatticeCommandResult[] commands =
        [
            firstAppend, insertedInMiddle, replacedInMiddle, removedInMiddle, removedLast,
            appendedAfterRemoval, replacedLast, duplicateTag, insertedAtEnd, lastAppend,
        ];
        Assert.All(commands, command => Assert.Throws<InvalidOperationException>(() => command.Changed));

        var committed = await update.CommitAsync();

        Assert.True(committed.Changed);
        Assert.Equal(new[] { true, true, true, true, true, true, true, false, true, true },
            commands.Select(command => command.Changed));
        Assert.True(items.TryGet("ada", out var stored));
        Assert.Equal<int>(expected, stored.Scores);
    }

    [Fact]
    public async Task An_invalid_position_after_appends_refuses_the_update_and_leaves_all_results_unavailable()
    {
        await using var database = await TestDatabases.OpenMasterAsync(UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        var original = item.Scores!.ToArray();
        var inserted = await items.InsertAsync(item);
        Assert.True(items.TryGet("ada", out _, out var originalRevision));

        var update = items.CreateUpdate("ada");
        var first = update.ListAppend("Scores", 30);
        var second = update.ListAppend("Scores", 40);
        var invalid = update.ListRemove("Scores", original.Length + 2);
        var last = update.ListAppend("Scores", 50);

        await TestData.ExpectErrorAsync(LatticeErrorEnum.UpdateTargetMissing, () => update.CommitAsync(originalRevision));

        Assert.All(new[] { first, second, invalid, last },
            command => Assert.Throws<InvalidOperationException>(() => command.Changed));
        Assert.Equal(inserted.Position, database.Position);
        Assert.True(items.TryGet("ada", out var stored, out var revision));
        Assert.Equal(originalRevision, revision);
        Assert.Equal(original, stored.Scores);
    }
}
