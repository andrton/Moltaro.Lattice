using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The database of the scenarios of Dictionary strings (brief of Epic 1, sprint S13, "Measured", and the
// planner's decision after the study, point 10), MemoryOnly, through the classes:
// - Item: the key Id, an Int64; Number, an Int64; Text, a String of the storage given, Plain or
//   Dictionary. The same entity with either storage, so that a read or a write of one compares with
//   the other by the storage alone; a Plain text of at most 64 bytes lies in the root, a longer one out
//   of line, and a Dictionary text in its entry.
// - Churn: the key Id, an Int64, and Data, a Binary: the entities a writer inserts and deletes beside the
//   reads, in the size class it chooses by the length of Data.
// The items are inserted with ids from 1, each with the text the function gives for its place.
internal sealed class TextItems : IDisposable
{
    private TextItems(EngineDatabase database, string storage, int count, string loaded)
    {
        Database = database;
        Storage = storage;
        Count = count;
        Loaded = loaded;
        Items = database.Set<Item>();
        Churns = database.Set<Churn>();
    }

    public EngineDatabase Database { get; }

    // Plain or Dictionary.
    public string Storage { get; }

    public int Count { get; }

    // The line of a report that says what was loaded, and how long it took.
    public string Loaded { get; }

    public ILatticeSet<Item> Items { get; }

    public ILatticeSet<Churn> Churns { get; }

    // Opens the database and inserts count items, item i with the text textOf(i - 1), in the encoding
    // given, under the inputs given or none.
    public static TextItems Load(string storage, int count, Func<int, string> textOf, string encoding = "Utf8", EngineTestInputs? inputs = null)
    {
        var database = EngineDatabase.Create(
            new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = SchemaOf(storage, encoding) },
            inputs ?? new EngineTestInputs());

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var items = database.Set<Item>();
        var loading = Stopwatch.StartNew();

        for (var id = 1; id <= count; id++)
        {
            items.InsertAsync(new Item { Id = id, Number = id, Text = textOf(id - 1) }).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        return new TextItems(database, storage, count, $"{storage}: {ReadSeries.Number(count)} items inserted in {ReadSeries.Seconds(loading.Elapsed)} s");
    }

    // Sixteen distinct texts of exactly this many bytes of ASCII, which differ in their first bytes.
    public static string[] Sixteen(int bytes) => [.. Enumerable.Range(0, 16).Select(index => Padded(string.Create(CultureInfo.InvariantCulture, $"text {index:D2}"), bytes))];

    // The text of the place among distinct ones, exactly this many bytes of ASCII.
    public static string Distinct(int place, int bytes) => Padded(place.ToString("D7", CultureInfo.InvariantCulture), bytes);

    // Keys of items drawn from 1 to Count by the generator of the fixture with the seed.
    public long[] Keys(ulong seed, int count)
    {
        var random = new FixtureRandom(seed);
        var keys = new long[count];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = random.Below(Count) + 1;
        }

        return keys;
    }

    // The bytes the arena holds for the entities, retired memory freed first: live and class slack.
    public long RetainedBytes()
    {
        Database.DrainRetired();

        var counters = Database.Counters;

        return counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes;
    }

    public void Dispose() => Database.Dispose();

    private static string Padded(string start, int bytes) =>
        start.Length >= bytes ? start[..bytes] : start + new string('x', bytes - start.Length);

    private static LatticeSchema SchemaOf(string storage, string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" },
                { "Id": 3, "Name": "Text", "Type": "String", "StringStorage": "{{storage}}" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Churn", "ClrType": "{{typeof(Churn).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Data", "Type": "Binary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public sealed class Item
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public string Text { get; set; } = "";
    }

    public sealed class Churn
    {
        public long Id { get; set; }

        public byte[] Data { get; set; } = [];
    }
}
