using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The figure that Epic 1, sprint S05 reports ("Measured"): the bytes the arena retains for one user
// and for one setting of the reference fixture without their collections, against the sum of their
// logical field sizes. It is the scalar form without its Unique marks, the form S05 can store.
//
// The users and the settings are written through RootWriter in prepared blocks and then published, as
// a write of the database writes a root, each entity type into an arena of its own, so that the
// counters of an arena are those of one entity type: the live bytes, the headers and payloads of the
// blocks; the class slack, what their size classes add to them; and the native capacity the arena
// obtained, Segments times the segment size, which also holds the tails of its regions and the slots
// and regions it has not filled. Each entity gets an entry in a RootTable, as
// the engine will give it one; the entries live in managed pages, outside the arena, and are
// reported apart. The logical size of a field is what its value takes in the scalar catalog: 8 bytes
// for an Int64, 1 for a Boolean, and the UTF-8 bytes of a String.
//
// It allocates and writes, and measures no time. The figures depend only on the data and the
// layout, not on the machine, except through the manifest that says where they were produced.
internal static class RootBytesScenario
{
    public const string Name = "root-bytes";

    // Far more than the entities of one type take, so that the limit plays no part.
    private const long ArenaMemoryLimit = 1L << 30;

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

        return report;
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
