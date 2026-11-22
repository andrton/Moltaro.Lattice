using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The database of the scenarios of secondary indexes, MemoryOnly, written and read through the class
// Item. It holds one entity type, Item, whose fields are the same in every schema, so that two schemas
// differ in the indexes they declare alone:
// - Id, the key, an Int64; Number, an Int64 in no index; Key1 to Key6, Int64; Title, a String of Plain
//   storage compared Ordinal.
// - A schema of k indexes declares ByKey1 to ByKeyk, one over each of the fields Key1 to Keyk, every one
//   of them unique in the schema of k unique indexes; the titled schema declares the one index ByTitle
//   over Title instead.
// An item carries one key in all six fields Key1 to Key6, so that a type with k indexes holds that key
// in each of its indexes, and a type with none holds the same fields.
//
// Ascending loads the items 1 to count, the key of each its id. Replacer replaces those items, each
// writer the places of its own: changing every key moves the key of an item between its id and its id
// plus count, so that its entry leaves its leaf for the other half of the tree, and the next change
// brings it back; keeping every key writes the key the item has. Either way Number changes, so that
// every replace changes the item. Which of its two keys each item has is kept here for the next writer
// of the item; the writers of one series take places of their own, and series run one after another.
internal sealed class IndexedItems : IDisposable
{
    public const string TitleIndex = "ByTitle";

    // Whether the item at the place has its moved key, its id plus Count; for Replacer.
    private readonly bool[] _moved;

    private IndexedItems(EngineDatabase database, int count, TimeSpan inserting, string loaded)
    {
        Database = database;
        Count = count;
        Inserting = inserting;
        Loaded = loaded;
        ByClass = database.Set<Item>();
        ByName = database.Set("Item");
        _moved = new bool[count];
    }

    public EngineDatabase Database { get; }

    public ILatticeSet<Item> ByClass { get; }

    public ILatticeSet ByName { get; }

    public int Count { get; }

    // How long the inserts of the load took, on the one thread that made them.
    public TimeSpan Inserting { get; }

    // The line of a report that says what was loaded, and how long it took.
    public string Loaded { get; }

    // The name of the index over the field Key1 to Key6.
    public static string KeyIndex(int field) => $"ByKey{field}";

    // Opens a database of the schema of so many key indexes, unique when unique is true, or the titled one,
    // under the memory limit of a read given or none, and inserts the items through the class, one after
    // the other on this thread.
    public static IndexedItems Load(int indexes, IReadOnlyList<Item> items, bool titled = false, long? readMemoryLimit = null, bool unique = false)
    {
        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = SchemaOf(indexes, titled, unique),
            ReadMemoryLimit = readMemoryLimit,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var set = database.Set<Item>();
        var inserting = Stopwatch.StartNew();

        foreach (var item in items)
        {
            Wait(set.InsertAsync(item));
        }

        inserting.Stop();

        var declared = titled ? $"the index {TitleIndex}" : $"{indexes} {(unique ? "unique " : "")}{(indexes == 1 ? "index" : "indexes")}";

        return new IndexedItems(database, items.Count, inserting.Elapsed, $"Database: the engine, MemoryOnly, {declared}; {ReadSeries.Number(items.Count)} items inserted through the class in {ReadSeries.Seconds(inserting.Elapsed)} s");
    }

    // The items 1 to count, the key of each its id, of k key indexes, unique when unique is true.
    public static IndexedItems Ascending(int indexes, int count, bool unique = false) =>
        Load(indexes, [.. Enumerable.Range(1, count).Select(id => Item.Keyed(id, id))], unique: unique);

    // A writer of the items of Ascending: caller c of n replaces the places c, c + n, c + 2n and so on,
    // and from c again after the last, one replace a call.
    public Func<int> Replacer(int caller, int callers, bool changeKeys)
    {
        var item = new Item();
        var place = caller;

        return () =>
        {
            if (changeKeys)
            {
                _moved[place] = !_moved[place];
            }

            item.Id = place + 1;
            item.Number++;
            item.SetKeys(_moved[place] ? place + 1L + Count : place + 1L);
            Replace(item);

            place += callers;

            if (place >= Count)
            {
                place = caller;
            }

            return 1;
        };
    }

    public void Insert(Item item) => Wait(ByClass.InsertAsync(item));

    public void Replace(Item item) => Wait(ByClass.ReplaceAsync(item));

    public void Delete(long id) => Wait(ByClass.DeleteAsync(id));

    // The bytes the arena holds, retired memory freed first: live and class slack, the nodes of the
    // indexes among them.
    public long RetainedBytes()
    {
        Database.DrainRetired();

        var counters = Database.Counters;

        return counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes;
    }

    public void Dispose() => Database.Dispose();

    // A write of MemoryOnly completes within its call; AsTask would allocate a task for it, which the
    // managed bytes of a write would count.
    private static void Wait(ValueTask<LatticeWriteResult> write)
    {
        if (write.IsCompletedSuccessfully)
        {
            _ = write.Result;
        }
        else
        {
            write.AsTask().GetAwaiter().GetResult();
        }
    }

    private static LatticeSchema SchemaOf(int indexes, bool titled, bool unique)
    {
        var mark = unique ? """, "Unique": true""" : "";
        IEnumerable<string> declared = titled
            ? [$$"""{ "Id": 1, "Name": "{{TitleIndex}}", "Fields": [9] }"""]
            : Enumerable.Range(1, indexes).Select(field => $$"""{ "Id": {{field}}, "Name": "{{KeyIndex(field)}}", "Fields": [{{field + 2}}]{{mark}} }""");

        return LatticeSchema.Compile($$"""
            {
              "StringEncoding": "Utf8",
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Number", "Type": "Int64" },
                    { "Id": 3, "Name": "Key1", "Type": "Int64" },
                    { "Id": 4, "Name": "Key2", "Type": "Int64" },
                    { "Id": 5, "Name": "Key3", "Type": "Int64" },
                    { "Id": 6, "Name": "Key4", "Type": "Int64" },
                    { "Id": 7, "Name": "Key5", "Type": "Int64" },
                    { "Id": 8, "Name": "Key6", "Type": "Int64" },
                    { "Id": 9, "Name": "Title", "Type": "String", "StringStorage": "Plain", "Comparison": "Ordinal" }
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [ {{string.Join(", ", declared)}} ]
                }
              ]
            }
            """);
    }

    public sealed class Item
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public long Key1 { get; set; }

        public long Key2 { get; set; }

        public long Key3 { get; set; }

        public long Key4 { get; set; }

        public long Key5 { get; set; }

        public long Key6 { get; set; }

        public string Title { get; set; } = "";

        // An item with the id, the key in all six key fields, and the title.
        public static Item Keyed(long id, long key, string title = "")
        {
            var item = new Item { Id = id, Title = title };

            item.SetKeys(key);

            return item;
        }

        public void SetKeys(long key)
        {
            Key1 = key;
            Key2 = key;
            Key3 = key;
            Key4 = key;
            Key5 = key;
            Key6 = key;
        }
    }
}
