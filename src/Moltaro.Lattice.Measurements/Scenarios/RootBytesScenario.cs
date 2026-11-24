using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The bytes the arena retains for one entity: the figure that Epic 1, sprint S05 reports ("Measured"), and
// beside it, in the same run, the figures of the owned values, Lists and references that sprint S18
// reports ("Measured"). It allocates and writes, and measures no time. The figures depend only on the data
// and the layout, not on the machine, except through the manifest that says where they were produced.
//
// The roots of the scalar form. The bytes the arena retains for one user and for one setting of the
// reference fixture without their collections, against the sum of their logical field sizes: the scalar
// form without its Unique marks. The users and the settings are written through RootWriter in prepared
// blocks and then published, as a write of the database writes a root, each entity type into an arena of
// its own, so that the counters of an arena are those of one entity type: the live bytes, the headers and
// payloads of the blocks; the class slack, what their size classes add to them; and the native capacity
// the arena obtained, Segments times the segment size, which also holds the tails of its regions and the
// slots and regions it has not filled. Each entity gets an entry in a RootTable, as the engine gives it
// one; the entries live in managed pages, outside the arena, and are reported apart. The logical size of a
// field is what its value takes in the scalar catalog: 8 bytes for an Int64, 1 for a Boolean, and the
// UTF-8 bytes of a String.
//
// Through the database. Every other figure is taken by writes of the engine database, MemoryOnly, by name
// and, where the entity type has a class, through it: the live bytes and the class slack the arena of the
// database gained by the inserts of one kind of entity, after a drain, divided by their number. That is
// the root of each entity and every block it owns, with the entries of the text table of its Dictionary
// texts, which every entity with the same text shares. The schemas declare no index, so that no node of an
// index is in the figure; no text here is long enough for an index to change where it lies (RootLayout).
// - The users and the groups of the fixture, in the scalar form without its Unique marks and in the full
//   form, whose users hold Groups, a List of three references to groups, whose groups hold Roles, a List
//   of two references to roles, and whose Region of a user is in Dictionary storage. The records are those
//   of the fixture, copied into its descriptor without indexes and with the classes of the fixture named;
//   the roles and the settings are not written.
// - An entity with an owned value absent and present, of a schema of the scenario's own.
// - An entity with a List in each of its two forms, packed, of Int64, and records, of String, at a few
//   sizes, each chosen for where its List lies (StructureLayout, ValueBlocks).
internal static class RootBytesScenario
{
    public const string Name = "root-bytes";

    // Far more than the entities of one type take, so that the limit plays no part.
    private const long ArenaMemoryLimit = 1L << 30;

    // The entities written for each case of the owned value, and for each size of a List.
    private const int OwnedEntities = 10_000;

    private const int ListEntities = 1_000;

    // The schema of the owned value and of the Lists: WithAddress holds an Address, a nullable owned value
    // of two Strings and an Int32, flattened into its root; WithNumbers holds a List of Int64, packed, and
    // WithTexts a List of String, whose element records hold a descriptor each.
    private const string NestedSchemaJson = """
        {
          "StringEncoding": "Utf8",
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address",
              "Fields": [
                { "Id": 1, "Name": "Street", "Type": "String" },
                { "Id": 2, "Name": "City", "Type": "String" },
                { "Id": 3, "Name": "Number", "Type": "Int32" }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "WithAddress",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Address", "Type": "Owned", "OwnedType": "Address", "Nullable": true }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "WithNumbers",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int64" } }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "WithTexts",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Texts", "Type": "List", "Element": { "Type": "String" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    // The sizes of the packed List of Int64, 8 bytes an element, which lies as the bytes of a Binary value:
    // inline up to 64 bytes, in one block up to 4 KiB, in chunks of 65,504 bytes above.
    private static readonly (int Count, string Where)[] PackedSizes =
    [
        (0, "empty, inline"),
        (4, "32 bytes, inline"),
        (100, "800 bytes, in one block"),
        (10_000, "80,000 bytes, in two chunks"),
    ];

    // The sizes of the List of String, of texts of 10 bytes, which lie inline in the block of their records.
    // A List of records is never inline in its root: no block when it is empty, one block up to the records
    // of a chunk, 65,504 / (16 + 64) = 818 records of one descriptor each, and chunks above.
    private static readonly (int Count, string Where)[] RecordSizes =
    [
        (0, "empty, no block"),
        (4, "in one block"),
        (100, "in one block"),
        (1_000, "in two chunks, of 818 and 182 records"),
    ];

    public static IReadOnlyList<string> Run()
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.ScalarSchemaJson(unique: false));
        var records = ReferenceFixture.Generate(schema).ToList();
        var report = new List<string>
        {
            $"Fixture: the scalar form without its Unique marks, seed {ReferenceFixture.Seed}, {Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
        };

        foreach (var name in new[] { "User", "Setting" })
        {
            report.Add("");
            report.AddRange(Measure(schema, name, records.Where(record => record.EntityType.Name == name)));
        }

        foreach (var full in new[] { false, true })
        {
            report.Add("");
            report.AddRange(FixtureForm(full));
        }

        report.Add("");
        report.AddRange(OwnedValues());
        report.Add("");
        report.AddRange(Lists());

        return report;
    }

    // The users and the groups of the fixture in one of its forms, by name and through their classes, each
    // way into a database of its own.
    private static List<string> FixtureForm(bool full)
    {
        var fixtureJson = full ? ReferenceFixture.SchemaJson : ReferenceFixture.ScalarSchemaJson(unique: false);
        var records = ReferenceFixture.Generate(LatticeSchema.Compile(fixtureJson)).ToList();
        var json = FixtureVariants.WithClass(fixtureJson, "User", full ? typeof(FixtureUserWithGroups) : typeof(FixtureUser));

        json = FixtureVariants.WithClass(json, "Group", full ? typeof(FixtureGroupWithRoles) : typeof(FixtureGroup));

        var schema = LatticeSchema.Compile(FixtureVariants.WithoutIndexes(json));
        var users = records.Where(record => record.EntityType.Name == "User").Select(user => FixtureVariants.CopyInto(schema, user)).ToList();
        var groups = records.Where(record => record.EntityType.Name == "Group").Select(group => FixtureVariants.CopyInto(schema, group)).ToList();
        var form = full ? "the full form" : "the scalar form without its Unique marks";
        var lines = new List<string>
        {
            $"Fixture through the database: {form}, without indexes, seed {ReferenceFixture.Seed}, {Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
        };

        using (var database = Open(schema))
        {
            lines.Add(Line("User, by name", schema, "User", users.Count, Retained(database, () => Insert(database.Set("User"), users))));
            lines.Add(Line("Group, by name", schema, "Group", groups.Count, Retained(database, () => Insert(database.Set("Group"), groups))));
        }

        using (var database = Open(schema))
        {
            var byClass = full
                ? (Users: Retained(database, () => Insert(database.Set<FixtureUserWithGroups>(), users.Select(FixtureObjects.UserWithGroups))),
                    Groups: Retained(database, () => Insert(database.Set<FixtureGroupWithRoles>(), groups.Select(FixtureObjects.GroupWithRoles))))
                : (Users: Retained(database, () => Insert(database.Set<FixtureUser>(), users.Select(FixtureObjects.User))),
                    Groups: Retained(database, () => Insert(database.Set<FixtureGroup>(), groups.Select(FixtureObjects.Group))));

            lines.Add(Line("User, through its class", schema, "User", users.Count, byClass.Users));
            lines.Add(Line("Group, through its class", schema, "Group", groups.Count, byClass.Groups));
        }

        return lines;
    }

    // An entity whose owned value is absent, and one whose owned value is present with texts that lie inline.
    private static List<string> OwnedValues()
    {
        var schema = LatticeSchema.Compile(NestedSchemaJson);

        if (!schema.TryGetOwnedType("Address", out var address))
        {
            throw new InvalidOperationException("The schema of the scenario has no owned type Address.");
        }

        using var database = Open(schema);

        var entities = database.Set("WithAddress");
        var id = 0L;
        var absent = Retained(database, () => Insert(entities, Enumerable.Range(0, OwnedEntities).Select(_ => new LatticeRecord(entities.EntityType) { ["Id"] = ++id })));
        var present = Retained(database, () => Insert(entities, Enumerable.Range(0, OwnedEntities).Select(_ => new LatticeRecord(entities.EntityType)
        {
            ["Id"] = ++id,
            ["Address"] = new LatticeOwnedValue(address) { ["Street"] = "Karl Johans gate 1", ["City"] = "Oslo", ["Number"] = 1 },
        })));

        return
        [
            "An owned value, flattened into its root, through the database by name",
            Line("WithAddress, the Address absent", schema, "WithAddress", OwnedEntities, absent),
            Line("WithAddress, the Address present, Street of 18 bytes and City of 4", schema, "WithAddress", OwnedEntities, present),
        ];
    }

    // An entity with a List of each form, at each of its sizes.
    private static List<string> Lists()
    {
        var schema = LatticeSchema.Compile(NestedSchemaJson);

        using var database = Open(schema);

        var numbers = database.Set("WithNumbers");
        var texts = database.Set("WithTexts");
        var id = 0L;
        var lines = new List<string> { "A List in each form, through the database by name" };

        foreach (var (count, where) in PackedSizes)
        {
            var values = Enumerable.Range(0, count).Select(index => (long)index).ToArray();
            var retained = Retained(database, () => Insert(numbers, Enumerable.Range(0, ListEntities).Select(_ => new LatticeRecord(numbers.EntityType) { ["Id"] = ++id, ["Numbers"] = values })));

            lines.Add(Line($"WithNumbers, packed, {Number(count)} Int64, {where}", schema, "WithNumbers", ListEntities, retained));
        }

        foreach (var (count, where) in RecordSizes)
        {
            var values = Enumerable.Range(0, count).Select(index => "text-" + index.ToString("D5", CultureInfo.InvariantCulture)).ToArray();
            var retained = Retained(database, () => Insert(texts, Enumerable.Range(0, ListEntities).Select(_ => new LatticeRecord(texts.EntityType) { ["Id"] = ++id, ["Texts"] = values })));

            lines.Add(Line($"WithTexts, records, {Number(count)} Strings of 10 bytes, {where}", schema, "WithTexts", ListEntities, retained));
        }

        return lines;
    }

    private static EngineDatabase Open(LatticeSchema schema)
    {
        var database = EngineDatabase.Create(
            new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = schema },
            new EngineTestInputs());

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        return database;
    }

    // The live bytes and the class slack the arena of the database gained by what insert wrote, with nothing
    // retired before or after.
    private static (long Live, long Slack) Retained(EngineDatabase database, Action insert)
    {
        database.DrainRetired();

        var before = database.Counters.Arena;

        insert();
        database.DrainRetired();

        var after = database.Counters.Arena;

        return (after.LiveBytes - before.LiveBytes, after.ClassSlackBytes - before.ClassSlackBytes);
    }

    private static void Insert(ILatticeSet set, IEnumerable<LatticeRecord> records)
    {
        foreach (var record in records)
        {
            set.InsertAsync(record).AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Insert<TEntity>(ILatticeSet<TEntity> set, IEnumerable<TEntity> entities)
        where TEntity : class
    {
        foreach (var entity in entities)
        {
            set.InsertAsync(entity).AsTask().GetAwaiter().GetResult();
        }
    }

    // The line of a figure taken through the database.
    private static string Line(string title, LatticeSchema schema, string entityTypeName, int count, (long Live, long Slack) retained)
    {
        if (!schema.TryGetEntityType(entityTypeName, out var entityType))
        {
            throw new InvalidOperationException($"The schema has no entity type {entityTypeName}.");
        }

        var fixedSize = RootLayout.Compile(entityType, schema).FixedSize;

        return $"  {title}: live {PerEntity(retained.Live, count)}, class slack {PerEntity(retained.Slack, count)}, retained {PerEntity(retained.Live + retained.Slack, count)} bytes per entity; {Number(count)} entities, fixed part of the root {fixedSize} bytes";
    }

    private static List<string> Measure(LatticeSchema schema, string entityTypeName, IEnumerable<LatticeRecord> records)
    {
        if (!schema.TryGetEntityType(entityTypeName, out var entityType))
        {
            throw new InvalidOperationException($"The fixture has no entity type {entityTypeName}.");
        }

        var layout = RootLayout.Compile(entityType, schema);
        var options = new ArenaOptions { MemoryLimit = ArenaMemoryLimit };
        using var arena = new Arena(options);
        var table = new RootTable();
        var logicalBytes = 0L;

        foreach (var record in records)
        {
            var handle = table.Add((uint)entityType.Id);

            table.Entry(handle).Root = Write(arena, layout, handle, record, ref logicalBytes);
        }

        var count = table.Count;
        var counters = arena.Counters;
        var capacity = (long)counters.Segments * options.SegmentSize;
        var retained = counters.LiveBytes + counters.ClassSlackBytes;
        var rootEntries = (long)table.Pages * RootTable.EntriesPerPage * Unsafe.SizeOf<RootEntry>();

        return
        [
            $"{entityTypeName}: {Number(count)} entities, fixed part of the root {layout.FixedSize} bytes",
            $"  logical field sizes  {PerEntity(logicalBytes, count)} bytes per entity",
            $"  live bytes           {PerEntity(counters.LiveBytes, count)} bytes per entity",
            $"  class slack          {PerEntity(counters.ClassSlackBytes, count)} bytes per entity",
            $"  retained             {PerEntity(retained, count)} bytes per entity, {Ratio(retained, logicalBytes)} times the logical size (live bytes and class slack)",
            $"  native capacity      {PerEntity(capacity, count)} bytes per entity, {Ratio(capacity, logicalBytes)} times the logical size ({counters.Segments} segments of {Number(options.SegmentSize)} bytes)",
            $"  of it in no counter  {PerEntity(capacity - retained, count)} bytes per entity (tails and free slots of regions, free regions, the reserve)",
            $"  root entries         {PerEntity(rootEntries, count)} bytes per entity in managed pages, outside the arena ({table.Pages} pages of {RootTable.EntriesPerPage} entries of {Unsafe.SizeOf<RootEntry>()} bytes)",
            $"  arena counters       live {Number(counters.LiveBytes)}, class slack {Number(counters.ClassSlackBytes)}, free regions {counters.FreeRegions}, reserved segments {counters.ReservedSegments}, segments {counters.Segments}",
        ];
    }

    // Writes the record as a root and adds the logical sizes of its fields.
    private static ArenaAddress Write(Arena arena, RootLayout layout, RootHandle handle, LatticeRecord record, ref long logicalBytes)
    {
        var writer = new RootWriter(arena, layout, handle);

        foreach (var field in record.EntityType.Fields)
        {
            switch (field.Declaration.Type)
            {
                case LatticeFieldTypeEnum.Int64:
                    writer.SetInteger(field, (long)record[field.Name]!);
                    logicalBytes += sizeof(long);
                    break;
                case LatticeFieldTypeEnum.Boolean:
                    writer.SetBoolean(field, (bool)record[field.Name]!);
                    logicalBytes += sizeof(bool);
                    break;
                case LatticeFieldTypeEnum.String:
                    var text = (string)record[field.Name]!;

                    writer.SetString(field, text);
                    logicalBytes += Encoding.UTF8.GetByteCount(text);
                    break;
                default:
                    throw new InvalidOperationException($"The scalar form of the fixture has no field of the type {field.Declaration.Type}.");
            }
        }

        var root = writer.Complete();

        RootStorage.Publish(arena, layout, root);

        return root;
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string PerEntity(long bytes, int count) => ((double)bytes / count).ToString("F2", CultureInfo.InvariantCulture).PadLeft(8);

    private static string Ratio(long bytes, long logicalBytes) => ((double)bytes / logicalBytes).ToString("F2", CultureInfo.InvariantCulture);
}
