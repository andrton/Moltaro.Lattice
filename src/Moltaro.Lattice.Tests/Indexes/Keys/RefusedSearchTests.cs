namespace Moltaro.Lattice.Tests.Indexes.Keys;

// What a search refuses instead of answering it some other way (decision on index access: the
// direct-index API rejects absent indexes and unsupported key shapes and does not fall back to a full
// scan; ILatticeIndex: nothing falls back to reading every entity).
// - An index the entity type does not declare cannot be selected: Index refuses the name with
//   BindingFailed, through a class and by name, also the name of an index another entity type
//   declares, before any key is given; so no object exists through which a search could read every
//   entity instead. A declared index is selected by its name in both forms.
public sealed class RefusedSearchTests
{
    [Fact]
    public async Task Index_the_entity_type_does_not_declare_is_refused_by_its_name_through_a_class_and_by_name()
    {
        await using var database = await TestData.OpenMasterAsync();

        // The two forms select a declared index by its name.
        Assert.Equal("ByEmail", database.Set<User>().Index("ByEmail").Definition.Name);
        Assert.Equal("ByEmail", database.Set("User").Index("ByEmail").Definition.Name);
        Assert.Equal("ByPriority", database.Set("Setting").Index("ByPriority").Definition.Name);

        // A field without an index, an index of another entity type, a name that differs in case.
        foreach (var name in new[] { "ByName", "ByPriority", "byemail" })
        {
            TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set<User>().Index(name));
            TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set("User").Index(name));
        }
    }
}
