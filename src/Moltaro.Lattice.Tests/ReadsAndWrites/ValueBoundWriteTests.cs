using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// One String or Binary value takes at most 268,173,376 bytes of its stored encoding, for text the
// encoding the schema declares in StringEncoding (the remarks of String and Binary of
// LatticeFieldTypeEnum). The bound is a validation of the contract (brief of Epic 1, sprint S06, the
// coordinator's note after the second review; the remarks of ILatticeSet<TEntity> and
// ILatticeWriteBatch): a write of an entity with a larger value, and adding one to a write batch, is
// ValidationFailed with the field named, through the class and by name, before the conditions of the
// write are looked at and before a write is refused as not implemented. The validation of the core
// checks it, so it holds on the placeholder and on the engine alike. A value of exactly the bound
// passes the validation; here it then meets a condition of the write or the refusal as not
// implemented, so that no test stores 268 MB.
//
// Where an encoding can be reached: in a UTF-8 schema an entity type of scalar fields is written on
// both implementations. In a UTF-16 schema no entity type is written yet, nor one with an enum field,
// so there the tests state that the validation comes before the refusal.
//
// The values are large. The text above the bound in UTF-8 is the one of ValueBound, 179 MB, built
// once in the test process: 268,173,377 bytes in UTF-8, 178,782,254 in UTF-16, below the bound. A
// value that only one test needs is made in its row and released with it: an array of 268,173,377
// or 268,173,376 bytes, whose bytes nothing reads before a write copies them, so they are not
// initialized; the text of exactly the bound in UTF-8, the same without its last character, 179 MB;
// and text above the bound in UTF-16, 134,086,689 times a, 268,173,378 bytes, 268 MB.
//
// A record copies a Binary value when it is set, and every write and every operation added to a batch
// copies it again, 268 MB, which is garbage once the write is refused or the batch is dropped. Left
// to itself, the runtime let those copies pile up to 4 GB within one test. So the tests collect the
// garbage after every write, and a row holds at most the value, the copy of its record and the copy
// of one write. And the class runs alone (LargeValueCollection), so that what it holds is not added
// to what other tests hold.
[Collection<LargeValueCollection>]
public sealed class ValueBoundWriteTests
{
    // The enum field Level, which neither implementation stores yet.
    private const string LevelField = """, { "Id": 4, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true }""";

    // The field of each type in Note, as the message names it.
    private static readonly Dictionary<LatticeFieldTypeEnum, string> Fields = new()
    {
        [LatticeFieldTypeEnum.String] = "Caption",
        [LatticeFieldTypeEnum.Binary] = "Attachment",
    };

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, LatticeFieldTypeEnum>> ImplementationsAndTypes =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from type in Fields.Keys
        select new TheoryDataRow<DatabaseImplementationEnum, LatticeFieldTypeEnum>(implementation, type);

    [Theory]
    [MemberData(nameof(ImplementationsAndTypes))]
    public async Task Write_of_a_value_above_the_bound_of_one_value_is_ValidationFailed_with_the_field_named_whatever_else_is_wrong_with_it(DatabaseImplementationEnum implementation, LatticeFieldTypeEnum type)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, NoteSchema("Utf8", ""));
        var notes = database.Set<Note>();
        var noteRecords = database.Set("Note");
        await notes.InsertAsync(new Note { Id = 1, Caption = "kept", Attachment = [1, 2, 3] });
        Assert.True(notes.TryGet(1L, out _, out var revision));
        var field = Fields[type];
        var value = AboveTheBound(type);
        Note NoteOf(long id) => NoteWith(id, type, value);

        // One record, whose key each write sets, so that a Binary value is copied into it once.
        var record = RecordWith(noteRecords.EntityType, 1, type, value);
        LatticeRecord RecordOf(long id)
        {
            record["Id"] = id;
            return record;
        }

        // The key of the stored entity on insert, an absent key on replace, a revision the entity
        // does not have, and an upsert of the stored entity and of an absent one: through the class,
        // with and without a session, and by name.
        await ExpectRefusedAsync(field, () => notes.InsertAsync(NoteOf(1)));
        await ExpectRefusedAsync(field, () => notes.InsertAsync(NoteOf(1), new LatticeSession()));
        await ExpectRefusedAsync(field, () => notes.ReplaceAsync(NoteOf(404)));
        await ExpectRefusedAsync(field, () => notes.ReplaceAsync(NoteOf(1), default(LatticeRevision)));
        await ExpectRefusedAsync(field, () => notes.ReplaceAsync(NoteOf(404), revision, new LatticeSession()));
        await ExpectRefusedAsync(field, () => notes.UpsertAsync(NoteOf(1)));
        await ExpectRefusedAsync(field, () => notes.UpsertAsync(NoteOf(2)));
        await ExpectRefusedAsync(field, () => noteRecords.InsertAsync(RecordOf(1)));
        await ExpectRefusedAsync(field, () => noteRecords.ReplaceAsync(RecordOf(404)));
        await ExpectRefusedAsync(field, () => noteRecords.ReplaceAsync(RecordOf(1), default(LatticeRevision)));
        await ExpectRefusedAsync(field, () => noteRecords.UpsertAsync(RecordOf(1), new LatticeSession()));
        await ExpectRefusedAsync(field, () => noteRecords.UpsertAsync(RecordOf(2)));

        // Added to a batch, through the class and by name.
        var batch = database.CreateWriteBatch();
        ExpectRefused(field, () => batch.Insert(NoteOf(1)));
        ExpectRefused(field, () => batch.Replace(NoteOf(404)));
        ExpectRefused(field, () => batch.Replace(NoteOf(1), default(LatticeRevision)));
        ExpectRefused(field, () => batch.Upsert(NoteOf(2)));
        ExpectRefused(field, () => batch.Insert(RecordOf(1)));
        ExpectRefused(field, () => batch.Replace(RecordOf(404)));
        ExpectRefused(field, () => batch.Upsert(RecordOf(2)));
        Assert.Equal(0, batch.Count);

        // Nothing was stored, and the stored entity is as it was.
        Assert.True(notes.TryGet(1L, out var stored, out var after));
        Assert.Equal("kept", stored.Caption);
        Assert.Equal(revision, after);
        Assert.Null(notes.Get(2L));
        Assert.Null(notes.Get(404L));
    }

    [Theory]
    [MemberData(nameof(ImplementationsAndTypes))]
    public async Task Value_above_the_bound_of_one_value_is_refused_before_the_write_is_refused_as_not_implemented(DatabaseImplementationEnum implementation, LatticeFieldTypeEnum type)
    {
        // An enum field: neither implementation stores such an entity type yet.
        await using var database = await TestDatabases.OpenMasterAsync(implementation, NoteSchema("Utf8", LevelField));
        var notes = database.Set<Note>();
        var noteRecords = database.Set("Note");
        var field = Fields[type];
        var value = AboveTheBound(type);
        var note = NoteWith(1, type, value);
        var record = RecordWith(noteRecords.EntityType, 1, type, value);

        await ExpectRefusedAsync(field, () => notes.InsertAsync(note));
        await ExpectRefusedAsync(field, () => notes.UpsertAsync(note));
        await ExpectRefusedAsync(field, () => noteRecords.InsertAsync(record));
        await ExpectRefusedAsync(field, () => noteRecords.UpsertAsync(record));
        ExpectRefused(field, () => database.CreateWriteBatch().Insert(note));
        ExpectRefused(field, () => database.CreateWriteBatch().Insert(record));

        // The same entity with a small value passes the validation and is refused as not
        // implemented.
        await ExpectNotImplementedAsync(() => notes.InsertAsync(NoteWith(1, type, Small(type))));
        await ExpectNotImplementedAsync(() => noteRecords.InsertAsync(RecordWith(noteRecords.EntityType, 1, type, Small(type))));
    }

    [Theory]
    [MemberData(nameof(ImplementationsAndTypes))]
    public async Task Value_of_exactly_the_bound_of_one_value_passes_the_validation(DatabaseImplementationEnum implementation, LatticeFieldTypeEnum type)
    {
        object value = type == LatticeFieldTypeEnum.String
            ? ValueBound.TextOneByteAboveInUtf8[..^1]
            : GC.AllocateUninitializedArray<byte>(ValueBound.Bytes);

        // An entity type that is stored: the write reaches its conditions, an absent key on replace
        // and the key of the stored entity on insert, through the class and by name, and the
        // operation is added to a batch.
        await using (var database = await TestDatabases.OpenMasterAsync(implementation, NoteSchema("Utf8", "")))
        {
            var notes = database.Set<Note>();
            var noteRecords = database.Set("Note");
            await notes.InsertAsync(new Note { Id = 1, Caption = "kept" });
            var record = RecordWith(noteRecords.EntityType, 404, type, value);

            await ExpectErrorAsync(LatticeErrorEnum.NotFound, () => notes.ReplaceAsync(NoteWith(404, type, value)));
            await ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => notes.InsertAsync(NoteWith(1, type, value)));
            await ExpectErrorAsync(LatticeErrorEnum.NotFound, () => noteRecords.ReplaceAsync(record));
            record["Id"] = 1L;
            await ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => noteRecords.InsertAsync(record));
            Assert.Equal(1, CountAfterAdding(database, batch => batch.Replace(NoteWith(404, type, value))));
            Assert.Equal(1, CountAfterAdding(database, batch => batch.Upsert(record)));
        }

        // An entity type that is not stored yet: the write reaches the refusal as not implemented.
        await using (var database = await TestDatabases.OpenMasterAsync(implementation, NoteSchema("Utf8", LevelField)))
        {
            var noteRecords = database.Set("Note");

            await ExpectNotImplementedAsync(() => database.Set<Note>().InsertAsync(NoteWith(1, type, value)));
            await ExpectNotImplementedAsync(() => noteRecords.InsertAsync(RecordWith(noteRecords.EntityType, 1, type, value)));
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Text_in_a_schema_of_the_UTF16_encoding_is_counted_in_UTF16_before_the_write_is_refused_as_not_implemented(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, NoteSchema("Utf16", ""));
        var notes = database.Set<Note>();
        var noteRecords = database.Set("Note");

        // Above the bound in UTF-8 and below it in UTF-16: it passes the validation, is refused as
        // not implemented and is added to a batch.
        var belowInUtf16 = ValueBound.TextOneByteAboveInUtf8;

        await ExpectNotImplementedAsync(() => notes.InsertAsync(new Note { Id = 1, Caption = belowInUtf16 }));
        await ExpectNotImplementedAsync(() => noteRecords.InsertAsync(RecordWith(noteRecords.EntityType, 1, LatticeFieldTypeEnum.String, belowInUtf16)));
        Assert.Equal(1, CountAfterAdding(database, batch => batch.Insert(new Note { Id = 1, Caption = belowInUtf16 })));

        // Above the bound in UTF-16 and half of it in UTF-8: ValidationFailed with the field named.
        var aboveInUtf16 = new string('a', (ValueBound.Bytes / 2) + 1);
        var record = RecordWith(noteRecords.EntityType, 1, LatticeFieldTypeEnum.String, aboveInUtf16);

        await ExpectRefusedAsync("Caption", () => notes.InsertAsync(new Note { Id = 1, Caption = aboveInUtf16 }));
        await ExpectRefusedAsync("Caption", () => noteRecords.InsertAsync(record));
        ExpectRefused("Caption", () => database.CreateWriteBatch().Insert(new Note { Id = 1, Caption = aboveInUtf16 }));
        ExpectRefused("Caption", () => database.CreateWriteBatch().Upsert(record));
    }

    // One byte above the bound: the text of ValueBound, or an array made for the row.
    private static object AboveTheBound(LatticeFieldTypeEnum type) => type == LatticeFieldTypeEnum.String
        ? ValueBound.TextOneByteAboveInUtf8
        : GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1);

    private static object Small(LatticeFieldTypeEnum type) => type == LatticeFieldTypeEnum.String ? "small" : new byte[] { 1, 2, 3 };

    // A Note whose field of the type holds the value, and whose other fields are null.
    private static Note NoteWith(long id, LatticeFieldTypeEnum type, object value)
    {
        if (type == LatticeFieldTypeEnum.String)
        {
            return new Note { Id = id, Caption = (string)value };
        }

        return new Note { Id = id, Attachment = (byte[])value };
    }

    private static LatticeRecord RecordWith(LatticeEntityType note, long id, LatticeFieldTypeEnum type, object value) =>
        new(note) { ["Id"] = id, [Fields[type]] = value };

    // Each of the four below collects the garbage after its write, so that the copy the write made
    // of a Binary value is gone before the next write makes another.
    private static async Task ExpectRefusedAsync(string field, Func<ValueTask<LatticeWriteResult>> write)
    {
        await TestData.ExpectValidationFailedAsync(field, write);
        GC.Collect();
    }

    private static void ExpectRefused(string field, Action add)
    {
        TestData.ExpectValidationFailed(field, add);
        GC.Collect();
    }

    private static async Task ExpectErrorAsync(LatticeErrorEnum error, Func<ValueTask<LatticeWriteResult>> write)
    {
        await TestData.ExpectErrorAsync(error, write);
        GC.Collect();
    }

    private static async Task ExpectNotImplementedAsync(Func<ValueTask<LatticeWriteResult>> write)
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await write());
        GC.Collect();
    }

    // The number of operations of a new batch after one is added. Then the batch is dropped, and the
    // values it took are collected.
    private static int CountAfterAdding(ILatticeDatabase database, Action<ILatticeWriteBatch> add)
    {
        ILatticeWriteBatch? batch = database.CreateWriteBatch();
        add(batch);
        var count = batch.Count;

        batch = null;
        GC.Collect();
        return count;
    }

    // The entity type Note, bound to the class Note, in a schema of the given encoding, with the
    // fields given after Id, Caption and Attachment and the enum type Level.
    private static LatticeSchema NoteSchema(string encoding, string fields) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "Enums": [ { "Id": 1, "Name": "Level", "UnderlyingType": "Int32", "Members": [ { "Name": "Low", "Value": 1 } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Caption", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Attachment", "Type": "Binary", "Nullable": true }{{fields}}
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Note
    {
        public long Id { get; set; }

        public string? Caption { get; set; }

        public byte[]? Attachment { get; set; }

        public int? Level { get; set; }
    }
}
