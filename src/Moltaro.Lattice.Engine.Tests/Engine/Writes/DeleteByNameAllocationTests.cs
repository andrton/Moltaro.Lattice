using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A delete by name takes its key without a box, as a delete through a class does (brief of Epic 1,
// sprint S09, part 2, the third of the findings of the outside review it carries, and Q51). A write
// allocates on its way through the write coordinator, so the test does not state a number of bytes:
// it states that a delete by name of an absent key, with an Int64 key given as itself and as an Int32
// and with a Guid key, and with either key given as an object the caller holds, boxed before the
// delete is measured, allocates no more managed memory than the same delete through a class. A box
// would add one object to every delete by name. Each form is measured in many rounds after rounds
// that took the one-time costs of its path, and the least of its rounds counts, so that what another
// thread of the process allocates meanwhile, which the count of the whole process includes, adds to
// no form; the count is that of the whole process, because a write may run on threads of the engine.
[Collection<WholeProcessAllocationCollection>]
public sealed class DeleteByNameAllocationTests
{
    private const int Rounds = 50;

    private static readonly Guid Absent = Guid.Parse("2b1f4c8e-03a9-4d55-8a0e-6f2d9b7c1e34");

    [Fact]
    public async Task Delete_by_name_of_an_absent_Int64_key_allocates_no_more_than_the_same_delete_through_a_class()
    {
        await using var database = await EngineMaster.OpenAsync(SchemaOf<long>("Int64"));
        var items = database.Set<Item<long>>();
        var records = database.Set("Item");
        object held = 2L;

        await ExpectNoMoreThanThroughAClassAsync(
            () => items.DeleteAsync(2L),
            ("an Int64", () => records.DeleteAsync(2L)),
            ("an Int32", () => records.DeleteAsync(2)),
            ("an object", () => records.DeleteAsync(held)));
    }

    [Fact]
    public async Task Delete_by_name_of_an_absent_Guid_key_allocates_no_more_than_the_same_delete_through_a_class()
    {
        await using var database = await EngineMaster.OpenAsync(SchemaOf<Guid>("Guid"));
        var items = database.Set<Item<Guid>>();
        var records = database.Set("Item");
        object held = Absent;

        await ExpectNoMoreThanThroughAClassAsync(
            () => items.DeleteAsync(Absent),
            ("a Guid", () => records.DeleteAsync(Absent)),
            ("an object", () => records.DeleteAsync(held)));
    }

    // Measures the delete through a class and each delete by name in turn, round after round, and
    // compares the least of each.
    private static async Task ExpectNoMoreThanThroughAClassAsync(
        Func<ValueTask<LatticeWriteResult>> throughAClass,
        params (string Form, Func<ValueTask<LatticeWriteResult>> Delete)[] byName)
    {
        // The one-time costs of every path.
        for (var round = 0; round < Rounds; round++)
        {
            await AllocatedAsync(throughAClass);

            foreach (var (_, delete) in byName)
            {
                await AllocatedAsync(delete);
            }
        }

        var leastThroughAClass = long.MaxValue;
        var leastByName = byName.Select(_ => long.MaxValue).ToArray();

        for (var round = 0; round < Rounds; round++)
        {
            leastThroughAClass = Math.Min(leastThroughAClass, await AllocatedAsync(throughAClass));

            for (var form = 0; form < byName.Length; form++)
            {
                leastByName[form] = Math.Min(leastByName[form], await AllocatedAsync(byName[form].Delete));
            }
        }

        for (var form = 0; form < byName.Length; form++)
        {
            Assert.True(
                leastByName[form] <= leastThroughAClass,
                $"A delete by name of an absent key given as {byName[form].Form} allocated {leastByName[form]} bytes, more than the {leastThroughAClass} of the same delete through a class.");
        }
    }

    // What the process allocates while the delete runs to its outcome, which is that the key is absent.
    private static async Task<long> AllocatedAsync(Func<ValueTask<LatticeWriteResult>> delete)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await delete();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.False(result.Changed);

        return allocated;
    }

    private static LatticeSchema SchemaOf<TKey>(string keyType) => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item<TKey>).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "{{keyType}}" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Item<TKey>
    {
        public TKey Id { get; set; } = default!;

        public string Text { get; set; } = "";
    }
}
