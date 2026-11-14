namespace Moltaro.Lattice.Tests;

// An object of a bound class is checked against the declarations of its nested values when it
// is taken for a write: a null where a position does not allow it, at any depth, and a key that
// its Map holds twice under the comparison of the key declaration. A write batch takes it when
// it is added, so the batch shows the check; an entity that violates the schema is refused
// there with ValidationFailed and the batch stays as it was.
public sealed class NestedObjectValidationTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' },
        { 'Id': 3, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true },
        { 'Id': 4, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } },
        { 'Id': 5, 'Name': 'Scores', 'Type': 'List', 'Element': { 'Type': 'Int32', 'Nullable': true } },
        { 'Id': 6, 'Name': 'Options', 'Type': 'Map', 'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }, 'Value': { 'Type': 'String' } },
        { 'Id': 7, 'Name': 'Rights', 'Type': 'Set', 'Element': { 'Type': 'String' } },
        { 'Id': 8, 'Name': 'Places', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Address' } }
        """;

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Object_whose_nested_values_fit_their_declarations_is_taken()
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<Contact>(Fields));
        var batch = database.CreateWriteBatch();

        // Null where a position allows it: a nullable owned value, a nullable element.
        batch.Insert(new Contact { Id = 1, Home = null, Scores = [1, null] });

        Assert.Equal(1, batch.Count);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Object_with_a_null_where_its_declaration_does_not_allow_it_is_refused()
    {
        await ExpectRefusedAsync(new Contact { Id = 1, Work = null! });
        await ExpectRefusedAsync(new Contact { Id = 1, Tags = null! });
        await ExpectRefusedAsync(new Contact { Id = 1, Tags = ["a", null!] });
        await ExpectRefusedAsync(new Contact { Id = 1, Options = new() { ["Theme"] = null! } });
        await ExpectRefusedAsync(new Contact { Id = 1, Rights = ["read", null!] });
        await ExpectRefusedAsync(new Contact { Id = 1, Places = [new BindingSchema.Address(), null!] });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Object_with_a_null_inside_an_owned_value_where_it_is_not_allowed_is_refused()
    {
        // City of Address is not nullable, nor are the elements of its Lines.
        await ExpectRefusedAsync(new Contact { Id = 1, Work = new BindingSchema.Address { City = null! } });
        await ExpectRefusedAsync(new Contact { Id = 1, Home = new BindingSchema.Address { Lines = [null!] } });
        await ExpectRefusedAsync(new Contact { Id = 1, Places = [new BindingSchema.Address { Corners = [null!] }] });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Object_whose_Map_holds_a_key_twice_under_the_comparison_of_the_key_declaration_is_refused()
    {
        // The Dictionary compares as Ordinal and holds two keys; the key declaration compares
        // as OrdinalIgnoreCase, for which they are one key given twice.
        var options = new Dictionary<string, string>(StringComparer.Ordinal) { ["Main"] = "1", ["main"] = "2" };

        await ExpectRefusedAsync(new Contact { Id = 1, Options = options });
    }

    private static async Task ExpectRefusedAsync(Contact contact)
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<Contact>(Fields));
        var batch = database.CreateWriteBatch();

        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Insert(contact));
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Upsert(contact));
        Assert.Equal(0, batch.Count);
    }

    private sealed class Contact
    {
        public long Id { get; set; }

        public BindingSchema.Address Work { get; set; } = new() { City = "Oslo" };

        public BindingSchema.Address? Home { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<int?> Scores { get; set; } = [];

        public Dictionary<string, string> Options { get; set; } = [];

        public HashSet<string> Rights { get; set; } = [];

        public List<BindingSchema.Address> Places { get; set; } = [];
    }
}
