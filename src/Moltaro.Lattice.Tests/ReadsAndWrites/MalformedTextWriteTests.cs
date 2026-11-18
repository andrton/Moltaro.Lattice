namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// By the planner's answer to Q41 (brief of Epic 1, sprint S06; the remarks of ILatticeSet<TEntity>
// and ILatticeWriteBatch): text that is not well-formed, a surrogate without its pair, in a String
// value is refused by the validation of a write like every other invalid value: ValidationFailed with
// the field named, for an object and for a record, at every depth, before the conditions of the write
// are looked at and before the write is refused as not implemented. The validation of the core makes
// the check, at a write and when an operation is added to a batch, so it holds also for an entity type
// the engine does not store yet. Such text is never
// stored and never replaced by another character, and a stored entity stays as it was.
//
// No entity type with an owned value or a collection is written yet, so there the tests state that the
// validation comes before the refusal. The cases of a UTF-16 schema and of an enum field are stated by
// Utf16DatabaseTests and EnumFieldTests.
public sealed class MalformedTextWriteTests
{
    // A surrogate without its pair, in its forms.
    private static readonly Dictionary<string, string> Forms = new()
    {
        ["a high surrogate at the end"] = "ab\uD83D",
        ["a high surrogate in the middle"] = "a\uD83Db",
        ["a low surrogate alone"] = "a\uDE00b",
        ["a pair in the wrong order"] = "a\uDE00\uD83Db",
    };

    // Where text lies in an entity of Holder (HolderSchema), and the field the message names.
    private static readonly Dictionary<string, (string Field, Func<LatticeSchema, string, LatticeRecord> Record, Func<string, Holder> Object)> Depths = new()
    {
        ["an element of a List"] = ("Tags", (schema, text) => HolderRecord(schema, tags: ["a", text]), text => new Holder { Tags = ["a", text] }),
        ["a key of a Map"] = ("Options", (schema, text) => HolderRecord(schema, options: new() { [text] = "v" }), text => new Holder { Options = new() { [text] = "v" } }),
        ["a value of a Map"] = ("Options", (schema, text) => HolderRecord(schema, options: new() { ["k"] = text }), text => new Holder { Options = new() { ["k"] = text } }),
        ["a member of a Set"] = ("Rights", (schema, text) => HolderRecord(schema, rights: ["read", text]), text => new Holder { Rights = ["read", text] }),
        ["a field of an owned value"] = ("City", (schema, text) => HolderRecord(schema, city: text), text => new Holder { Work = new Address { City = text } }),
        ["an element of a List in an owned value"] = ("Lines", (schema, text) => HolderRecord(schema, lines: [text]), text => new Holder { Work = new Address { Lines = [text] } }),
        ["a field of an owned value in a List"] = ("City", (schema, text) => HolderRecord(schema, place: text), text => new Holder { Places = [new Address { City = text }] }),
    };

    public static IEnumerable<TheoryDataRow<string>> FormCases =>
        Forms.Keys.Select(form => new TheoryDataRow<string>(form));

    public static IEnumerable<TheoryDataRow<string>> DepthCases =>
        Depths.Keys.Select(depth => new TheoryDataRow<string>(depth));

    [Fact]
    public async Task Write_of_text_that_is_not_well_formed_is_ValidationFailed_with_the_field_named_whatever_else_is_wrong_with_it()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org" });
        Assert.True(users.TryGet(1, out _, out var revision));
        const string Bad = "ada\uD83D@example.org";
        User UserOf(long id) => new() { Id = id, Name = "Ada", Email = Bad };
        LatticeRecord RecordOf(long id) => new(userRecords.EntityType) { ["Id"] = id, ["Name"] = "Ada", ["Email"] = Bad };

        // The key of the stored entity on insert, an absent key on replace, a revision the entity
        // does not have, and an upsert of the stored entity and of an absent one: through the class,
        // with and without a session, and by name.
        await TestData.ExpectValidationFailedAsync("Email", () => users.InsertAsync(UserOf(1)));
        await TestData.ExpectValidationFailedAsync("Email", () => users.InsertAsync(UserOf(1), new LatticeSession()));
        await TestData.ExpectValidationFailedAsync("Email", () => users.ReplaceAsync(UserOf(404)));
        await TestData.ExpectValidationFailedAsync("Email", () => users.ReplaceAsync(UserOf(1), default(LatticeRevision)));
        await TestData.ExpectValidationFailedAsync("Email", () => users.ReplaceAsync(UserOf(404), revision, new LatticeSession()));
        await TestData.ExpectValidationFailedAsync("Email", () => users.UpsertAsync(UserOf(1)));
        await TestData.ExpectValidationFailedAsync("Email", () => users.UpsertAsync(UserOf(2)));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.InsertAsync(RecordOf(1)));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.ReplaceAsync(RecordOf(404)));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.ReplaceAsync(RecordOf(1), default(LatticeRevision)));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.UpsertAsync(RecordOf(1), new LatticeSession()));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.UpsertAsync(RecordOf(2)));

        // Added to a batch, through the class and by name.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Email", () => batch.Insert(UserOf(1)));
        TestData.ExpectValidationFailed("Email", () => batch.Replace(UserOf(404)));
        TestData.ExpectValidationFailed("Email", () => batch.Replace(UserOf(1), default(LatticeRevision)));
        TestData.ExpectValidationFailed("Email", () => batch.Upsert(UserOf(2)));
        TestData.ExpectValidationFailed("Email", () => batch.Insert(RecordOf(1)));
        TestData.ExpectValidationFailed("Email", () => batch.Replace(RecordOf(404)));
        TestData.ExpectValidationFailed("Email", () => batch.Upsert(RecordOf(2)));
        Assert.Equal(0, batch.Count);

        // Nothing was stored, and the stored entity is as it was.
        Assert.True(users.TryGet(1, out var stored, out var after));
        Assert.Equal("ada@example.org", stored.Email);
        Assert.Equal(revision, after);
        Assert.Null(users.Get(2));
        Assert.Null(users.Get(404));
    }

    [Theory]
    [MemberData(nameof(FormCases))]
    public async Task Every_form_of_a_surrogate_without_its_pair_is_refused_and_a_pair_in_its_order_is_stored(string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        var bad = Forms[form];

        await TestData.ExpectValidationFailedAsync("Email", () => users.InsertAsync(new User { Id = 1, Name = "Ada", Email = bad }));
        await TestData.ExpectValidationFailedAsync("Email", () => userRecords.InsertAsync(new LatticeRecord(userRecords.EntityType) { ["Id"] = 2L, ["Name"] = "Ada", ["Email"] = bad }));
        TestData.ExpectValidationFailed("Email", () => database.CreateWriteBatch().Upsert(new User { Id = 3, Name = "Ada", Email = bad }));

        Assert.Empty(users.Enumerate());

        // A surrogate with its pair, in its order, is text like any other.
        await users.InsertAsync(new User { Id = 4, Name = "Ada", Email = "a😀b" });

        Assert.Equal("a😀b", users.Get(4)!.Email);
    }

    [Theory]
    [MemberData(nameof(DepthCases))]
    public async Task Text_that_is_not_well_formed_at_any_depth_is_refused_before_the_write_is_refused_as_not_implemented(string depth)
    {
        await using var database = await TestDatabases.OpenMasterAsync(HolderSchema());
        var holders = database.Set<Holder>();
        var holderRecords = database.Set("Holder");
        var (field, record, entity) = Depths[depth];
        var bad = Forms["a high surrogate in the middle"];

        await TestData.ExpectValidationFailedAsync(field, () => holderRecords.InsertAsync(record(database.Schema, bad)));
        await TestData.ExpectValidationFailedAsync(field, () => holderRecords.UpsertAsync(record(database.Schema, bad)));
        await TestData.ExpectValidationFailedAsync(field, () => holders.InsertAsync(entity(bad)));
        await TestData.ExpectValidationFailedAsync(field, () => holders.ReplaceAsync(entity(bad)));
        TestData.ExpectValidationFailed(field, () => database.CreateWriteBatch().Insert(record(database.Schema, bad)));
        TestData.ExpectValidationFailed(field, () => database.CreateWriteBatch().Upsert(entity(bad)));

        // The same entity with well-formed text passes the validation and is refused as not
        // implemented.
        await Assert.ThrowsAsync<NotSupportedException>(async () => await holderRecords.InsertAsync(record(database.Schema, "well-formed")));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await holders.InsertAsync(entity("well-formed")));
    }

    // Holder holds text at every depth: in a List, a Map, a Set, an owned value of Address, a List
    // in it, and a List of owned values. The engine does not store it yet.
    private static LatticeSchema HolderSchema() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [ { "Id": 1, "Name": "City", "Type": "String" }, { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(Holder).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 3, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String" } },
                { "Id": 4, "Name": "Rights", "Type": "Set", "Element": { "Type": "String" } },
                { "Id": 5, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 6, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // A record of Holder with valid values but where the depth under test is given.
    private static LatticeRecord HolderRecord(
        LatticeSchema schema,
        string[]? tags = null,
        Dictionary<string, string>? options = null,
        string[]? rights = null,
        string city = "Oslo",
        string[]? lines = null,
        string? place = null)
    {
        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        Assert.True(schema.TryGetOwnedType("Address", out var address));

        return new LatticeRecord(holder)
        {
            ["Id"] = 1L,
            ["Tags"] = tags ?? new[] { "a" },
            ["Options"] = options ?? new Dictionary<string, string> { ["k"] = "v" },
            ["Rights"] = rights ?? new[] { "read" },
            ["Work"] = new LatticeOwnedValue(address) { ["City"] = city, ["Lines"] = lines ?? new[] { "Karl Johans gate 1" } },
            ["Places"] = place is null ? Array.Empty<LatticeOwnedValue>() : new[] { new LatticeOwnedValue(address) { ["City"] = place, ["Lines"] = Array.Empty<string>() } },
        };
    }

    private sealed class Holder
    {
        public long Id { get; set; } = 1;

        public List<string> Tags { get; set; } = ["a"];

        public Dictionary<string, string> Options { get; set; } = new() { ["k"] = "v" };

        public HashSet<string> Rights { get; set; } = ["read"];

        public Address Work { get; set; } = new();

        public List<Address> Places { get; set; } = [];
    }

    private sealed class Address
    {
        public string City { get; set; } = "Oslo";

        public List<string> Lines { get; set; } = ["Karl Johans gate 1"];
    }
}
