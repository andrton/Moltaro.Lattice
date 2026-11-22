using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A child forwards its writes and does not reserve a value of a unique index itself: the master decides,
// also about a value the child has not seen taken yet, and the child reports the refusal with the name of
// the index (decision on unique indexes, "Children forward writes and do not reserve a globally unique
// value locally"), for a record by name and for a class alike, an insert and a replace, and nothing of the
// refused write is applied on either node. Accounts of UniqueSchemas.
public sealed class ThroughAChildTests
{
    [Fact]
    public async Task Write_through_a_child_is_refused_by_the_master_with_the_name_of_the_index_by_name_and_through_a_class()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, UniqueSchemas.Accounts);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var byName = child.Set("Account");
        var byClass = child.Set<Account>();

        await master.Set<Account>().InsertAsync(new Account { Id = 1, Login = "ada", Tenant = "acme", Email = "ada@example.org" });
        await byClass.InsertAsync(new Account { Id = 2, Login = "grace", Tenant = "acme", Email = "grace@example.org" });

        var record = new LatticeRecord(byName.EntityType) { ["Id"] = 3L, ["Login"] = "ada", ["Tenant"] = "other", ["Email"] = null };

        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => byName.InsertAsync(record));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => byClass.InsertAsync(new Account { Id = 3, Login = "ada", Tenant = "other", Email = null }));
        await UniqueSchemas.ExpectTakenAsync("ByTenantAndEmail", () => byClass.ReplaceAsync(new Account { Id = 2, Login = "grace", Tenant = "acme", Email = "ada@example.org" }));

        var position = master.Position;

        await child.WaitForPositionAsync(position, Bound);

        Assert.Null(master.Set<Account>().Get(3L));
        Assert.Null(byClass.Get(3L));
        Assert.Equal("grace@example.org", master.Set<Account>().Get(2L)!.Email);
        Assert.Equal("grace@example.org", byClass.Get(2L)!.Email);
        Assert.Equal([1L], UniqueSchemas.IdsOf(byClass.Index("ByLogin").Find(IndexKey.Equal("ada"))));
    }
}
