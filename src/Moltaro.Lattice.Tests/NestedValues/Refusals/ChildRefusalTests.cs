namespace Moltaro.Lattice.Tests.NestedValues.Refusals;

// A child does not receive entities with owned values, Lists or references yet (ILatticeDatabase.OpenAsync):
// a master that stores such an entity type refuses every child when it connects, the open of the child
// fails with NotSupportedException and the child is closed, while the master stays Ready and serves its
// entities.
public sealed class ChildRefusalTests
{
    [Fact]
    public async Task Child_of_a_master_that_stores_owned_values_Lists_or_references_is_refused_and_the_master_stays_Ready()
    {
        await using var master = await NestedSchema.OpenListeningMasterAsync();

        await master.Set("Person").InsertAsync(NestedSchema.RecordOf(master.Schema, NestedSchema.Full("ada")));

        var child = TestDatabases.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
        });

        await Assert.ThrowsAsync<NotSupportedException>(async () => await child.OpenAsync());

        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, master.State);
        Assert.NotNull(master.Set("Person").Get("ada"));
    }
}
