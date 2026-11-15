namespace Moltaro.Lattice.Tests.EnumFields;

// Whether an integer is a value its enum type declares is checked by the validation of the core
// (Q03.5, Q03.6; brief of Epic 1, sprint S07), with every other validation of a value and in the
// order of Q25, on the placeholder and on the engine alike; LatticeEnumType.IsValid is the rule. A
// value that an ordinary enum type does not declare, and for a Flags type a bit that no member
// declares, is ValidationFailed with the field named: by class, where a .NET enum property holds such
// a value by a cast, and by name; through a set and when added to a batch; at any depth; before the
// conditions of the write are looked at and before the write is refused as not implemented. For a
// Flags type zero is valid, for an ordinary enum type only when it is declared; null is valid where
// the field is nullable. A value set into a record is not checked: the write checks it. A missing
// required value and a null in a field that is not nullable are refused in the same way.
//
// Alarm has the key Id, a required Text and four Enum fields: Level, of an ordinary UInt8 enum type
// without zero; Grade, nullable, of an ordinary Int16 enum type with zero; Rights, of a Flags UInt64
// enum type whose highest declared bit is the 64th; and Signal, nullable, of a Flags Int8 enum type
// that declares its sign bit. Panel holds a List of Level. The placeholder stores neither, so there a
// valid value ends in NotSupportedException after the validation, and so it does for Panel on the
// engine until S18; the engine stores Alarm from this sprint.
public sealed class EnumValidationTests
{
    private enum LevelEnum : byte
    {
        Low = 1,
        High = 2,
    }

    private enum GradeEnum : short
    {
        None = 0,
        Minor = 1,
        Major = 2,
    }

    [Flags]
    private enum RightsEnum : ulong
    {
        Read = 1,
        Write = 2,
        Export = 4,
        Audit = 0x8000_0000_0000_0000,
    }

    [Flags]
    private enum SignalEnum : sbyte
    {
        Ready = 1,
        Sign = sbyte.MinValue,
    }

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "Enums": [
            { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 1 }, { "Name": "High", "Value": 2 } ] },
            { "Id": 2, "Name": "Grade", "UnderlyingType": "Int16", "Members": [ { "Name": "None", "Value": 0 }, { "Name": "Minor", "Value": 1 }, { "Name": "Major", "Value": 2 } ] },
            {
              "Id": 3, "Name": "Rights", "UnderlyingType": "UInt64", "Flags": true,
              "Members": [ { "Name": "Read", "Value": 1 }, { "Name": "Write", "Value": 2 }, { "Name": "Export", "Value": 4 }, { "Name": "Audit", "Value": 9223372036854775808 } ]
            },
            { "Id": 4, "Name": "Signal", "UnderlyingType": "Int8", "Flags": true, "Members": [ { "Name": "Ready", "Value": 1 }, { "Name": "Sign", "Value": -128 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Alarm", "ClrType": "{{typeof(Alarm).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String" },
                { "Id": 3, "Name": "Level", "Type": "Enum", "Enum": "Level" },
                { "Id": 4, "Name": "Grade", "Type": "Enum", "Enum": "Grade", "Nullable": true },
                { "Id": 5, "Name": "Rights", "Type": "Enum", "Enum": "Rights" },
                { "Id": 6, "Name": "Signal", "Type": "Enum", "Enum": "Signal", "Nullable": true }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Panel", "ClrType": "{{typeof(Panel).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Levels", "Type": "List", "Element": { "Type": "Enum", "Enum": "Level" } } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // A value its enum type does not allow, the field it is in, and how a record and an object get it.
    private static readonly Dictionary<string, (string Field, Action<LatticeRecord> Record, Action<Alarm> Object)> Faults = new()
    {
        ["an integer the ordinary enum type does not declare"] = ("Level", record => record["Level"] = 3, alarm => alarm.Level = (LevelEnum)3),
        ["zero for an ordinary enum type that does not declare it"] = ("Level", record => record["Level"] = 0, alarm => alarm.Level = 0),
        ["an integer the enum type of a nullable field does not declare"] = ("Grade", record => record["Grade"] = 3, alarm => alarm.Grade = (GradeEnum)3),
        ["a bit that no member of the Flags type declares"] = ("Rights", record => record["Rights"] = 8, alarm => alarm.Rights = (RightsEnum)8),
        ["declared bits together with one that no member declares"] = ("Rights", record => record["Rights"] = 0x8000_0000_0000_0011ul, alarm => alarm.Rights = RightsEnum.Read | RightsEnum.Audit | (RightsEnum)16),
        ["a bit that no member of a signed Flags type declares"] = ("Signal", record => record["Signal"] = 2, alarm => alarm.Signal = (SignalEnum)2),
    };

    // A value its enum type allows, and how a record and an object get it.
    private static readonly Dictionary<string, (Action<LatticeRecord> Record, Action<Alarm> Object)> Allowed = new()
    {
        ["zero for a Flags type"] = (record => record["Rights"] = 0, alarm => alarm.Rights = 0),
        ["zero for an ordinary enum type that declares it"] = (record => record["Grade"] = 0, alarm => alarm.Grade = GradeEnum.None),
        ["null where the field is nullable"] = (NullsIn, NullsIn),
        ["declared bits that no member names together"] = (record => record["Rights"] = 0x8000_0000_0000_0005ul, alarm => alarm.Rights = RightsEnum.Read | RightsEnum.Export | RightsEnum.Audit),
        ["the sign bit of a signed Flags type with another bit"] = (record => record["Signal"] = -127, alarm => alarm.Signal = SignalEnum.Sign | SignalEnum.Ready),
    };

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> ImplementationsAndFaults =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from fault in Faults.Keys
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, fault);

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> ImplementationsAndAllowed =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from allowed in Allowed.Keys
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, allowed);

    [Theory]
    [MemberData(nameof(ImplementationsAndFaults))]
    public async Task Write_with_a_value_its_enum_type_does_not_allow_is_ValidationFailed_with_the_field_named(DatabaseImplementationEnum implementation, string fault)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);
        var alarms = database.Set<Alarm>();
        var records = database.Set("Alarm");
        var (field, faultOfRecord, faultOfObject) = Faults[fault];

        LatticeRecord RecordOf(long id)
        {
            var record = ValidRecord(records, id);
            faultOfRecord(record);
            return record;
        }

        Alarm ObjectOf(long id)
        {
            var alarm = new Alarm { Id = id };
            faultOfObject(alarm);
            return alarm;
        }

        // Through a set, by name and by class: an insert and an upsert of an absent key, which would
        // otherwise succeed or be refused as not implemented, and a replace.
        await TestData.ExpectValidationFailedAsync(field, () => records.InsertAsync(RecordOf(1)));
        await TestData.ExpectValidationFailedAsync(field, () => records.UpsertAsync(RecordOf(1)));
        await TestData.ExpectValidationFailedAsync(field, () => records.ReplaceAsync(RecordOf(1)));
        await TestData.ExpectValidationFailedAsync(field, () => alarms.InsertAsync(ObjectOf(1)));
        await TestData.ExpectValidationFailedAsync(field, () => alarms.UpsertAsync(ObjectOf(1), new LatticeSession()));
        await TestData.ExpectValidationFailedAsync(field, () => alarms.ReplaceAsync(ObjectOf(1)));

        // Added to a batch, by name and by class.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed(field, () => batch.Insert(RecordOf(1)));
        TestData.ExpectValidationFailed(field, () => batch.Replace(RecordOf(2)));
        TestData.ExpectValidationFailed(field, () => batch.Upsert(ObjectOf(3)));
        TestData.ExpectValidationFailed(field, () => batch.Replace(ObjectOf(4), default(LatticeRevision)));
        Assert.Equal(0, batch.Count);

        Assert.Empty(records.Enumerate());
    }

    [Theory]
    [MemberData(nameof(ImplementationsAndAllowed))]
    public async Task Value_its_enum_type_allows_passes_the_validation(DatabaseImplementationEnum implementation, string allowed)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);
        var records = database.Set("Alarm");
        var (allowedOfRecord, allowedOfObject) = Allowed[allowed];
        var record = ValidRecord(records, 1);
        var alarm = new Alarm { Id = 2 };

        allowedOfRecord(record);
        allowedOfObject(alarm);

        // Adding to a batch validates as a write does, and writes nothing on either implementation.
        // That the engine stores these values is stated by EnumFieldTests.
        var batch = database.CreateWriteBatch();
        batch.Insert(record);
        batch.Upsert(alarm);

        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public void Value_set_into_a_record_is_not_checked_against_its_enum_type()
    {
        Assert.True(Schema.TryGetEntityType("Alarm", out var alarm));
        Assert.True(Schema.TryGetEntityType("Panel", out var panel));

        var record = new LatticeRecord(alarm) { ["Level"] = 3, ["Grade"] = (short)3, ["Rights"] = 8ul, ["Signal"] = (sbyte)2 };
        var levels = new LatticeRecord(panel) { ["Levels"] = new byte[] { 1, 3 } };

        Assert.Equal((byte)3, record["Level"]);
        Assert.Equal((short)3, record["Grade"]);
        Assert.Equal(8ul, record["Rights"]);
        Assert.Equal((sbyte)2, record["Signal"]);
        Assert.Equal(new byte[] { 1, 3 }, levels.Get<List<byte>>("Levels"));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Value_its_enum_type_does_not_allow_inside_a_collection_is_ValidationFailed_before_the_write_is_refused(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);
        var panels = database.Set<Panel>();
        var records = database.Set("Panel");
        var record = new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Levels"] = new byte[] { 1, 3 } };
        var panel = new Panel { Id = 1, Levels = [LevelEnum.Low, (LevelEnum)3] };

        await TestData.ExpectValidationFailedAsync("Levels", () => records.InsertAsync(record));
        await TestData.ExpectValidationFailedAsync("Levels", () => panels.UpsertAsync(panel));
        TestData.ExpectValidationFailed("Levels", () => database.CreateWriteBatch().Insert(record));
        TestData.ExpectValidationFailed("Levels", () => database.CreateWriteBatch().Insert(panel));

        // With declared values the write passes the validation and is refused as not implemented: no
        // implementation stores a List yet.
        record["Levels"] = new byte[] { 1, 2 };
        panel.Levels = [LevelEnum.Low, LevelEnum.High];
        await Assert.ThrowsAsync<NotSupportedException>(async () => await records.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await panels.InsertAsync(panel));
    }

    [Fact]
    public async Task Value_its_enum_type_does_not_allow_is_refused_before_the_conditions_of_the_write()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema);
        var alarms = database.Set<Alarm>();
        var records = database.Set("Alarm");
        var stored = new Alarm { Id = 1, Text = "stored", Level = LevelEnum.High, Grade = GradeEnum.None, Rights = 0, Signal = SignalEnum.Sign | SignalEnum.Ready };
        await alarms.InsertAsync(stored);
        Assert.True(alarms.TryGet(1L, out _, out var revision));

        // An insert of the stored key, which would otherwise be DuplicateKey; a replace of an absent
        // key, which would otherwise be NotFound; a replace with a revision the entity does not have,
        // which would otherwise be RevisionConflict; and an upsert of the stored key, which would
        // otherwise replace it.
        await TestData.ExpectValidationFailedAsync("Level", () => alarms.InsertAsync(new Alarm { Id = 1, Level = (LevelEnum)3 }));
        await TestData.ExpectValidationFailedAsync("Level", () => records.ReplaceAsync(RecordWith(records, 404, "Level", 3)));
        await TestData.ExpectValidationFailedAsync("Rights", () => alarms.ReplaceAsync(new Alarm { Id = 1, Rights = (RightsEnum)8 }, default(LatticeRevision)));
        await TestData.ExpectValidationFailedAsync("Signal", () => records.UpsertAsync(RecordWith(records, 1, "Signal", 2), new LatticeSession()));

        ExpectAsStored(alarms, stored, revision);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Missing_required_value_and_a_null_in_a_field_that_is_not_nullable_are_ValidationFailed_with_the_field_named(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);
        var alarms = database.Set<Alarm>();
        var records = database.Set("Alarm");

        // A record without Level, a record with a null in Rights, an object with a null in Text.
        var withoutLevel = new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Text"] = "alarm", ["Rights"] = 0ul };
        var nullRights = RecordWith(records, 1, "Rights", null);
        var nullText = new Alarm { Id = 1, Text = null! };

        await TestData.ExpectValidationFailedAsync("Level", () => records.InsertAsync(withoutLevel));
        await TestData.ExpectValidationFailedAsync("Rights", () => records.UpsertAsync(nullRights));
        await TestData.ExpectValidationFailedAsync("Text", () => alarms.ReplaceAsync(nullText));

        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Level", () => batch.Upsert(withoutLevel));
        TestData.ExpectValidationFailed("Rights", () => batch.Insert(nullRights));
        TestData.ExpectValidationFailed("Text", () => batch.Insert(nullText));
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public async Task Missing_required_value_and_a_null_in_a_field_that_is_not_nullable_are_refused_before_the_conditions_of_the_write()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema);
        var alarms = database.Set<Alarm>();
        var records = database.Set("Alarm");
        var stored = new Alarm { Id = 1, Text = "stored" };
        await alarms.InsertAsync(stored);
        Assert.True(alarms.TryGet(1L, out _, out var revision));

        var withoutLevel = new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Text"] = "alarm", ["Rights"] = 0ul };

        await TestData.ExpectValidationFailedAsync("Level", () => records.InsertAsync(withoutLevel));
        await TestData.ExpectValidationFailedAsync("Rights", () => records.ReplaceAsync(RecordWith(records, 404, "Rights", null)));
        await TestData.ExpectValidationFailedAsync("Text", () => alarms.ReplaceAsync(new Alarm { Id = 1, Text = null! }, default(LatticeRevision)));

        ExpectAsStored(alarms, stored, revision);
    }

    // A record of Alarm whose values are all valid: Text, Level and Rights are set, Grade and Signal
    // are null.
    private static LatticeRecord ValidRecord(ILatticeSet records, long id) =>
        new(records.EntityType) { ["Id"] = id, ["Text"] = "alarm", ["Level"] = (byte)LevelEnum.Low, ["Rights"] = 0ul };

    // Null in both nullable Enum fields.
    private static void NullsIn(LatticeRecord record)
    {
        record["Grade"] = null;
        record["Signal"] = null;
    }

    private static void NullsIn(Alarm alarm)
    {
        alarm.Grade = null;
        alarm.Signal = null;
    }

    private static LatticeRecord RecordWith(ILatticeSet records, long id, string field, object? value)
    {
        var record = ValidRecord(records, id);
        record[field] = value;
        return record;
    }

    // Nothing of the refused writes was stored.
    private static void ExpectAsStored(ILatticeSet<Alarm> alarms, Alarm stored, LatticeRevision revision)
    {
        Assert.True(alarms.TryGet(1L, out var read, out var after));
        Assert.Equal(revision, after);
        Assert.Equal(stored.Text, read.Text);
        Assert.Equal(stored.Level, read.Level);
        Assert.Equal(stored.Grade, read.Grade);
        Assert.Equal(stored.Rights, read.Rights);
        Assert.Equal(stored.Signal, read.Signal);
        Assert.Null(alarms.Get(404L));
        Assert.Single(alarms.Enumerate());
    }

    private sealed class Alarm
    {
        public long Id { get; set; }

        public string Text { get; set; } = "alarm";

        // Zero is not a value of Level.
        public LevelEnum Level { get; set; } = LevelEnum.Low;

        public GradeEnum? Grade { get; set; }

        public RightsEnum Rights { get; set; }

        public SignalEnum? Signal { get; set; }
    }

    private sealed class Panel
    {
        public long Id { get; set; }

        public List<LevelEnum> Levels { get; set; } = [LevelEnum.Low];
    }
}
