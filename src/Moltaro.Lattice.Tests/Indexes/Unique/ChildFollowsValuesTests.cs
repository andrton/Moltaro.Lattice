using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A child checks its unique indexes once it is ready and every change it receives from then on, and the
// history of a master never violates them (decision on unique indexes, "Child application validates complete
// transaction state before acknowledging it"): so a child loads a database in which several roots hold a key
// with a null part, follows every valid change of the values of a unique index — a key with a null part
// held again and again, a value given up and taken by another entity in the next write, a replace that keeps
// a key in another case, a value deleted and inserted again — and keeps following after them, with the same
// owners as its master. Slots and Profiles of UniqueSchemas.
public sealed class ChildFollowsValuesTests
{
    [Fact]
    public async Task Child_loads_and_follows_keys_with_a_null_part_held_by_several_roots_and_a_complete_key_that_changes_owner()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, UniqueSchemas.Slots);
        var slots = master.Set("Slot");

        for (var id = 1L; id <= 3; id++)
        {
            await slots.InsertAsync(UniqueSchemas.RecordOf(slots, id, ("A", null), ("B", 2), ("C", "c")));
            await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 10 + id, ("A", "a"), ("B", null), ("C", null)));
        }

        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 20, ("A", "a"), ("B", 2), ("C", "c")));

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var copies = child.Set("Slot");

        // The owner of the complete key gives it up for a key with a null part, and another entity takes it in
        // the next write; more roots take the keys with a null part.
        await slots.ReplaceAsync(UniqueSchemas.RecordOf(slots, 20, ("A", "a"), ("B", null), ("C", null)));
        await slots.ReplaceAsync(UniqueSchemas.RecordOf(slots, 1, ("A", "a"), ("B", 2), ("C", "c")));
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 4, ("A", null), ("B", 2), ("C", "c")));
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 30, ("A", null), ("B", null), ("C", null)));
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 31, ("A", null), ("B", null), ("C", null)));

        await child.WaitForPositionAsync(master.Position, Bound);

        foreach (var set in new[] { slots, copies })
        {
            Assert.Equal([1L], UniqueSchemas.IdsOf(set.Index("ByABC").Find(IndexKey.Equal("a", 2, "c"))));
            Assert.Equal([2L, 3L, 4L], UniqueSchemas.IdsOf(set.Index("ByABC").Find(IndexKey.Equal<string?, int, string>(null, 2, "c"))));
            Assert.Equal([11L, 12L, 13L, 20L], UniqueSchemas.IdsOf(set.Index("ByABC").Find(IndexKey.Equal<string, int?, string?>("a", null, null))));
            Assert.Equal([30L, 31L], UniqueSchemas.IdsOf(set.Index("ByABC").Find(IndexKey.Equal<string?, int?, string?>(null, null, null))));
        }

        // The child still follows.
        await slots.DeleteAsync(1L);
        await child.WaitForPositionAsync(master.Position, Bound);
        Assert.Null(copies.Get(1L));
    }

    [Fact]
    public async Task Child_follows_a_value_kept_in_another_case_given_up_taken_deleted_and_taken_again()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, UniqueSchemas.Profiles);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var profiles = master.Set("Profile");
        var copies = child.Set("Profile");

        await profiles.InsertAsync(Profile(profiles, 1, "one", "Ada"));
        await profiles.ReplaceAsync(Profile(profiles, 1, "one", "ADA"));
        await profiles.ReplaceAsync(Profile(profiles, 1, "one", "Bob"));
        await profiles.InsertAsync(Profile(profiles, 2, "two", "ada"));
        await profiles.ReplaceAsync(Profile(profiles, 1, "two-later", "bOB"));
        await profiles.DeleteAsync(2L);
        await profiles.InsertAsync(Profile(profiles, 3, "two", "aDa"));
        await profiles.ReplaceAsync(Profile(profiles, 3, "three", "aDa"));
        await profiles.InsertAsync(Profile(profiles, 2, "two", "Cy"));

        await child.WaitForPositionAsync(master.Position, Bound);

        foreach (var set in new[] { profiles, copies })
        {
            Assert.Equal([3L], UniqueSchemas.IdsOf(set.Index("ByNick").Find(IndexKey.Equal("ADA"))));
            Assert.Equal([1L], UniqueSchemas.IdsOf(set.Index("ByNick").Find(IndexKey.Equal("bob"))));
            Assert.Equal([2L], UniqueSchemas.IdsOf(set.Index("ByLogin").Find(IndexKey.Equal("two"))));
            Assert.Equal([3L], UniqueSchemas.IdsOf(set.Index("ByLogin").Find(IndexKey.Equal("three"))));
            Assert.Equal("bOB", set.Get(1L)!["Nick"]);
        }

        // The child still follows.
        await profiles.DeleteAsync(3L);
        await child.WaitForPositionAsync(master.Position, Bound);
        Assert.Null(copies.Get(3L));
    }

    private static LatticeRecord Profile(ILatticeSet set, long id, string login, string nick) =>
        UniqueSchemas.RecordOf(set, id, ("Login", login), ("Nick", nick));
}
