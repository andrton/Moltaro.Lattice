using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches;

// The schema of the tests of a write batch in the engine: Item, the key Id, an Int64; Tag, a String in the
// index ByTag; Group, an Int64 in the index ByGroup; Login, a String in the unique index ByLogin. A test
// states what a batch does to each of the three, and what a reader of each finds while it is published.
//
// The batch the tests of visibility publish (Members), over the state Before: items 1 to 6 and 100 hold the
// tag "old" in group 1, but 3 and 4, which hold "doomed"; 100 is no member. The batch replaces 2 and 1 into
// the tag "new" and group 2, inserts 11 and 12 there, and deletes 4 and 3, in the order Members gives: the
// replace of 2 comes first, so that the first change, whose stores a publication held after it has made,
// lies after another member in the order of every index the members share.
internal static class BatchSchema
{
    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tag", "Type": "String" },
                { "Id": 3, "Name": "Group", "Type": "Int64" },
                { "Id": 4, "Name": "Login", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByTag", "Fields": [2] },
                { "Id": 2, "Name": "ByGroup", "Fields": [3] },
                { "Id": 3, "Name": "ByLogin", "Fields": [4], "Unique": true }
              ]
            }
          ]
        }
        """);

    // The members inserted, replaced into the new tag and deleted, and the item that is no member.
    public static readonly long[] Inserted = [11, 12];

    public static readonly long[] Replaced = [1, 2];

    public static readonly long[] Deleted = [3, 4];

    public const long Other = 100;

    // The items that hold the new tag and group 2 once the batch is published, in the order of the
    // identities of their root entries, which every index of one key follows: the replaced ones hold the
    // slots of their first inserts, before those of the items the batch inserts.
    public static readonly long[] InNewTag = [1, 2, 11, 12];

    public static Item Of(long id, string tag, long group) => new() { Id = id, Tag = tag, Group = group, Login = $"login {id}" };

    // Inserts the state before the batch.
    public static async Task InsertBeforeAsync(ILatticeSet<Item> items)
    {
        foreach (var id in new long[] { 1, 2, 3, 4, 5, 6, Other })
        {
            await items.InsertAsync(Of(id, id is 3 or 4 ? "doomed" : "old", group: 1));
        }
    }

    // The batch, in the order of its members.
    public static ILatticeWriteBatch Members(ILatticeDatabase database)
    {
        var batch = database.CreateWriteBatch();

        batch.Replace(Of(2, "new", group: 2));
        batch.Insert(Of(11, "new", group: 2));
        batch.Delete<Item>(4L);
        batch.Replace(Of(1, "new", group: 2));
        batch.Insert(Of(12, "new", group: 2));
        batch.Delete<Item>(3L);

        return batch;
    }

    // The state of the item after the batch, null for an item it deletes; for an item it does not change,
    // its state before.
    public static (string Tag, long Group)? After(long id) => id switch
    {
        1 or 2 or 11 or 12 => ("new", 2),
        3 or 4 => null,
        _ => ("old", 1),
    };

    // The state of the item before the batch, null for an item it inserts.
    public static (string Tag, long Group)? Before(long id) => id switch
    {
        11 or 12 => null,
        3 or 4 => ("doomed", 1),
        _ => ("old", 1),
    };

    public static (string Tag, long Group)? StateOf(Item? item) => item is null ? null : (item.Tag, item.Group);

    internal sealed class Item
    {
        public long Id { get; set; }

        public string Tag { get; set; } = "";

        public long Group { get; set; }

        public string Login { get; set; } = "";
    }
}
