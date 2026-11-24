using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// A write refused in the middle of its tree leaves nothing behind: what it placed before the refusal, values
// out of line, blocks of records and the chunks of their texts, at every depth, is freed, and the ownership
// audit finds every block owned by exactly one live entity (OwnershipAudit). The trees are of WideTreeSchema.
// - Refused for memory, in an arena of three segments of 4 MiB: 200 Tags of 70,000 characters, about 14 MB,
//   each in chunks of its own; and a List of 60 Outer whose Inner holds three Wide with a first text of
//   70,000 characters. Two of those Outer, in the same arena, are written.
// - Refused with ValidationFailed by the guard of the writer on the bound of a List: the second Outer holds
//   4,095 Wide, one more than a List of a record of one chunk may hold, after the first Outer and its text in
//   chunks were placed; nothing of the entity stays live.
public sealed class RefusalInsideATreeTests
{
    [Fact]
    public async Task Arena_refusal_while_the_records_of_a_List_are_filled_leaves_nothing_behind_at_any_depth()
    {
        await using var database = await EngineMaster.OpenAsync(WideTreeSchema.Schema, new EngineTestInputs { Arena = new ArenaOptions { MemoryLimit = 3L * 4 * 1024 * 1024 } });
        var items = database.Set("Item");

        await items.InsertAsync(WideTreeSchema.ItemOf(items, 1, ["small"]));
        OwnershipAudit.Expect(database);

        var tags = Enumerable.Range(0, 200).Select(index => new string((char)('a' + (index % 26)), 70_000)).ToArray();
        var refused = await Assert.ThrowsAsync<LatticeException>(() => items.InsertAsync(WideTreeSchema.ItemOf(items, 2, tags)).AsTask());

        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        OwnershipAudit.Expect(database);

        var rows = Enumerable.Range(0, 60)
            .Select(row => WideTreeSchema.OuterOf(Enumerable.Range(0, 3).Select(_ => WideTreeSchema.WideOf(new string('x', 70_000 + row)))))
            .ToArray();

        refused = await Assert.ThrowsAsync<LatticeException>(() => items.InsertAsync(WideTreeSchema.ItemOf(items, 3, ["t"], rows)).AsTask());

        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        OwnershipAudit.Expect(database);

        await items.InsertAsync(WideTreeSchema.ItemOf(items, 4, ["t"], rows[..2]));
        OwnershipAudit.Expect(database);
    }

    [Fact]
    public async Task List_above_its_bound_deep_in_a_tree_is_ValidationFailed_and_leaves_nothing_behind()
    {
        await using var database = await EngineMaster.OpenAsync(WideTreeSchema.Schema);
        var items = database.Set("Item");

        var rows = new[]
        {
            WideTreeSchema.OuterOf([WideTreeSchema.WideOf(new string('y', 70_000))]),
            WideTreeSchema.OuterOf(Enumerable.Range(0, 4_095).Select(_ => WideTreeSchema.WideOf(""))),
        };

        var refused = await Assert.ThrowsAsync<LatticeException>(() => items.InsertAsync(WideTreeSchema.ItemOf(items, 1, ["t"], rows)).AsTask());

        Assert.Equal(LatticeErrorEnum.ValidationFailed, refused.Error);
        OwnershipAudit.Expect(database);
        Assert.Equal(0, database.Counters.Arena.LiveBytes);
    }
}
