namespace Moltaro.Lattice.Tests.Indexes;

// What a search refuses instead of answering it some other way (decision on index access: the
// direct-index API rejects absent indexes and unsupported key shapes and does not fall back to a full
// scan; ILatticeIndex: nothing falls back to reading every entity).
// - An index the entity type does not declare cannot be selected: Index refuses the name with
//   BindingFailed, through a class and by name, also the name of an index another entity type
//   declares, before any key is given; so no object exists through which a search could read every
//   entity instead. A declared index is selected by its name in both forms.
// - The search by the leading fields of an index, a range, the reverse order, Offset and Limit come
//   after the search by the full key; until they are built each is refused with NotSupportedException,
//   in both forms, Find and FindAsync, and is not answered by reading the entities instead. A full key
//   with the options of a search in reverse or of a page is refused alike.
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

    public static IEnumerable<TheoryDataRow<string>> LaterShapes =>
    [
        new("the leading field of two"),
        new("no field at all"),
        new("a range after the leading field"),
        new("a range on the first field"),
    ];

    [Theory]
    [MemberData(nameof(LaterShapes))]
    public async Task Search_by_leading_fields_or_with_a_range_is_refused_as_not_supported_in_both_forms(string shape)
    {
        await using var database = await OpenWithAdaAsync();
        var key = KeyOf(shape);
        var byClass = database.Set<User>().Index("ByAgeAndName");
        var byName = database.Set("User").Index("ByAgeAndName");

        Assert.Throws<NotSupportedException>(() => byClass.Find(key).ToList());
        Assert.Throws<NotSupportedException>(() => byName.Find(key).ToList());
        await Assert.ThrowsAsync<NotSupportedException>(async () => await byClass.FindAsync(key, new LatticeFindOptions()));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await byName.FindAsync(key, new LatticeFindOptions()));
    }

    public static IEnumerable<TheoryDataRow<string>> LaterOptions =>
    [
        new("reverse"),
        new("offset"),
        new("limit"),
    ];

    [Theory]
    [MemberData(nameof(LaterOptions))]
    public async Task Search_by_the_full_key_in_reverse_or_with_Offset_or_Limit_is_refused_as_not_supported_in_both_forms(string option)
    {
        await using var database = await OpenWithAdaAsync();
        var options = option switch
        {
            "reverse" => new LatticeFindOptions { Reverse = true },
            "offset" => new LatticeFindOptions { Offset = 1 },
            _ => new LatticeFindOptions { Limit = 1 },
        };
        var key = IndexKey.Equal(36, "Ada");

        await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Set<User>().Index("ByAgeAndName").FindAsync(key, options));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Set("User").Index("ByAgeAndName").FindAsync(key, options));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Set<User>().Index("ByEmail").FindAsync(IndexKey.Equal("ada@example.org"), options));
    }

    private static IndexKey KeyOf(string shape) => shape switch
    {
        "the leading field of two" => IndexKey.Equal(36),
        "no field at all" => IndexKey.All,
        "a range after the leading field" => IndexKey.Equal(36).Range(IndexBound.Inclusive("A"), null),
        _ => IndexKey.All.Range(IndexBound.Inclusive(30), IndexBound.Exclusive(40)),
    };

    private static async Task<ILatticeDatabase> OpenWithAdaAsync()
    {
        var database = await TestData.OpenMasterAsync();

        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org", Age = 36 });

        return database;
    }
}
