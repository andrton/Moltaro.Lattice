namespace Moltaro.Lattice.Tests.Indexes;

// An element of the Fields of an index is one field identity or a list of identities. A list
// of one identity is the field itself: [[2]] and [2] declare the same part, with the same
// fingerprint. An empty list declares no field. An index names a part once, in either
// spelling; two paths that differ in any step are different parts.
public sealed class IndexPartFormTests
{
    private const string Fields = "'Fields': [20, [3, 3]]";

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Part_written_as_a_list_of_one_identity_is_the_field_itself()
    {
        var original = ValueSchema.Compile();
        var spelled = LatticeSchema.Compile(Descriptor.With(ValueSchema.Json, Fields, "'Fields': [[20], [3, 3]]"));

        Assert.True(ValueSchema.User(spelled).TryGetIndex("ByLoginAndWorkFloor", out var index));
        var login = ValueSchema.Field(spelled, "Login");
        Assert.Equal([login], index.Parts[0].Path);
        Assert.Same(login, index.Parts[0].Field);

        Assert.Equal(original.Fingerprint, spelled.Fingerprint);
        Assert.Equal(ValueSchema.User(original).LayoutFingerprint, ValueSchema.User(spelled).LayoutFingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Part_of_a_field_of_the_entity_type_has_the_same_fingerprint_in_either_spelling()
    {
        // A schema of Epic 1, sprint 2, with the parts of ByAgeAndName written as lists.
        var spelled = LatticeSchema.Compile(Descriptor.With(TestData.SchemaJson, "'Fields': [4, 2]", "'Fields': [[4], [2]]"));

        Assert.Equal(TestData.Schema.Fingerprint, spelled.Fingerprint);
        Assert.True(spelled.TryGetEntityType("User", out var user));
        Assert.True(user.TryGetIndex("ByAgeAndName", out var byAgeAndName));
        Assert.Equal(["Age", "Name"], byAgeAndName.Parts.Select(part => Assert.Single(part.Path).Name));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("'Fields': [[], [3, 3]]")]
    [InlineData("'Fields': [20, []]")]
    [InlineData("'Fields': [ [] ]")]
    public void Empty_path_is_refused_with_the_index_named(string fields)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, Fields, fields);

        Assert.Contains("ByLoginAndWorkFloor", exception.Message);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // A path twice, and a field twice in the two spellings.
    [InlineData("'Fields': [20, [3, 3], [3, 3]]")]
    [InlineData("'Fields': [20, [20]]")]
    [InlineData("'Fields': [[20], 20]")]
    [InlineData("'Fields': [[20], [20], [3, 3]]")]
    [InlineData("'Fields': [20, [3, 3], 20]")]
    public void Same_part_twice_in_either_spelling_is_refused_with_the_index_named(string fields)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, Fields, fields);

        Assert.Contains("ByLoginAndWorkFloor", exception.Message);
    }
}
