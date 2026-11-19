namespace Moltaro.Lattice.Tests.Indexes;

// What a key and a bound have to be for an index to use them, checked against the declaration of the
// index before anything is searched (IndexKey, IndexBound; ILatticeIndex: anything an index cannot serve
// is an ArgumentException). These are checks of the contract and need no search: each is stated through
// a class and by name, by Find and by FindAsync.
// - A value has the type of its field; a value of another type, or of a type no field has, is refused.
// - A key has at most as many values as the index has fields.
// - Null is a value only of a part that can be null.
// - A bound is made by Inclusive or Exclusive; the default of IndexBound has no value, which reading it
//   tells, and a range with it is refused.
// - The default of IndexKey is no key: a search with it is refused, and IndexKey.All is the one key that
//   asks for every entry of an index. Reading what a default key holds is refused as reading the value of
//   a default bound is; IndexKey has no public member that reads it, so that sentence has no test here.
public sealed class IndexKeyArgumentTests
{
    public static IEnumerable<TheoryDataRow<string>> WrongValues =>
    [
        new("a Guid for an Int32 field"),
        new("a Double for an Int32 field"),
        new("an integer for a String field"),
        new("a value of a type no field has"),
        new("an integer beyond the range of the field"),
    ];

    [Theory]
    [MemberData(nameof(WrongValues))]
    public async Task Value_of_another_type_than_its_field_is_refused_in_both_forms(string value)
    {
        await using var database = await TestData.OpenMasterAsync();
        var key = value switch
        {
            "a Guid for an Int32 field" => IndexKey.Equal(Guid.NewGuid(), "Ada"),
            "a Double for an Int32 field" => IndexKey.Equal(36.0, "Ada"),
            "an integer for a String field" => IndexKey.Equal(36, 36),
            "a value of a type no field has" => IndexKey.Equal(36, new Uri("https://example.org")),
            _ => IndexKey.Equal(long.MaxValue, "Ada"),
        };

        await ExpectRefusedAsync(database, "ByAgeAndName", key);
    }

    [Fact]
    public async Task Key_with_more_values_than_the_index_has_fields_is_refused_in_both_forms()
    {
        await using var database = await TestData.OpenMasterAsync();

        await ExpectRefusedAsync(database, "ByEmail", IndexKey.Equal("ada@example.org", "ada@example.org"));
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal(36, "Ada", "Lovelace"));
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal(36, "Ada", true, 1));
    }

    [Fact]
    public async Task Null_for_a_part_that_is_never_null_is_refused_in_both_forms()
    {
        await using var database = await TestData.OpenMasterAsync();

        // Name is never null; Age and Email may be.
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal<int?, string?>(36, null));
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal<int?, string?>(null, null));
    }

    [Fact]
    public async Task Default_bound_has_no_value_and_a_range_with_it_is_refused_in_both_forms()
    {
        await using var database = await TestData.OpenMasterAsync();
        var none = default(IndexBound);

        Assert.Throws<InvalidOperationException>(() => none.Value);
        Assert.False(none.IsInclusive);

        // A bound that Inclusive or Exclusive made holds its value as it was given.
        Assert.Equal(36, IndexBound.Inclusive(36).Value);
        Assert.True(IndexBound.Inclusive(36).IsInclusive);
        Assert.Equal("Ada", IndexBound.Exclusive("Ada").Value);
        Assert.False(IndexBound.Exclusive("Ada").IsInclusive);

        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal(36).Range(none, null));
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.Equal(36).Range(IndexBound.Inclusive("A"), none));
        await ExpectRefusedAsync(database, "ByAgeAndName", IndexKey.All.Range(none, IndexBound.Exclusive(40)));
    }

    [Fact]
    public async Task Default_key_is_refused_in_both_forms_and_IndexKey_All_is_not_the_default()
    {
        await using var database = await TestData.OpenMasterAsync();
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org", Age = 36 });

        foreach (var index in new[] { "ByEmail", "ByAgeAndName" })
        {
            await ExpectRefusedAsync(database, index, default);

            // All asks for every entry of the index: it is not refused as a key the index cannot serve.
            var whole = Record.Exception(() => database.Set<User>().Index(index).Find(IndexKey.All).ToList());

            Assert.False(whole is ArgumentException, $"IndexKey.All was refused as the default key is: {whole}");
        }
    }

    // The key is refused with ArgumentException through a class and by name, by Find and by FindAsync,
    // before anything is searched.
    private static async Task ExpectRefusedAsync(ILatticeDatabase database, string index, IndexKey key)
    {
        var byClass = database.Set<User>().Index(index);
        var byName = database.Set("User").Index(index);

        Assert.Throws<ArgumentException>(() => byClass.Find(key).ToList());
        Assert.Throws<ArgumentException>(() => byName.Find(key).ToList());
        await Assert.ThrowsAsync<ArgumentException>(async () => await byClass.FindAsync(key, new LatticeFindOptions()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await byName.FindAsync(key, new LatticeFindOptions()));
    }
}
