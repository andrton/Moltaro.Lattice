using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Tests.Storage.Ordered;

// How the entries and the nodes of a secondary index lie in the arena (docs/design/ordered-index.md,
// "Nodes in the arena"; IndexLayout):
// - the header of a node is 8 bytes, which keep the children of an inner node on 8-byte boundaries;
// - an entry is the identity of 8 bytes, a null bitmap of one byte for up to eight parts that can be null,
//   and the parts: a fixed part at the width its root stores it at, a String part one head byte and a copy
//   of its text when it has 64 bytes or less, and the head, the length of 4 bytes and the address of 8 of
//   the text entry its root references when it is longer. So an entry of a key of 4 bytes takes 12, of 8
//   bytes 16, of 16 bytes 24, of a text copied of L bytes 9 + L, of a shared text 21;
// - a node is 1 KiB, the size class of 1,024 bytes with its header, unless four of the widest entries of
//   its index do not fit a leaf of that size, or four of its widest separators with their five children an
//   inner node; it then takes the smallest size class that holds them. The widest entry has every String
//   part a copy of 64 bytes. Up to the largest block: an index of 251 String parts takes nodes of 64 KiB.
// Each entry is written from a root the test writes into an arena of its own (RootArena), and its bytes
// are those WriteEntry returns and EntryLength reads back from it.
public sealed class IndexLayoutTests
{
    // A node starts with its header; a leaf of an index with a String part has an offset of 2 bytes for
    // each entry, an inner node a child of 8 bytes for each separator and one more.
    private const int Header = 8;

    private const int Offset = 2;

    private const int Child = 8;

    [Fact]
    public void Header_of_a_node_is_eight_bytes()
    {
        Assert.Equal(8, NodeHeader.Size);
        Assert.Equal(NodeHeader.Size, Unsafe.SizeOf<NodeHeader>());
    }

    public static IEnumerable<TheoryDataRow<string, int>> Entries =>
    [
        new("Int32", 12),
        new("DateOnly", 12),
        new("Int64", 16),
        new("Float64", 16),
        new("DateTimeOffset", 16),
        new("Guid", 24),
        new("Decimal", 24),
        new("nullable Int64", 17),
        new("text of 0 bytes", 9),
        new("text of 8 bytes", 17),
        new("text of 64 bytes", 73),
        new("text of 65 bytes", 21),
        new("text of 1,000 bytes", 21),
        new("Dictionary text of 64 bytes", 73),
        new("Dictionary text of 65 bytes", 21),
        new("null text", 10),
    ];

    [Theory(Skip = SkipReason.BeforeSprint14)]
    [MemberData(nameof(Entries))]
    public void Entry_takes_its_identity_its_null_bitmap_and_its_part_as_the_design_lays_them_out(string part, int expected)
    {
        var (declaration, value) = PartOf(part);

        using var item = new RootArena(SchemaOf(declaration, "[2]"));
        var layout = IndexLayout.Compile(Index(item), item.Layout);
        var writer = item.Writer();
        var field = item.Field("Part");

        writer.SetInteger(item.Field("Id"), 1);
        value(writer, field);
        var root = writer.Complete();
        var entry = new byte[layout.WidestEntryBytes + IndexLayout.SharedTextBytes];
        var bytes = layout.WriteEntry(item.Reader(root), RootArena.Handle, entry);

        Assert.Equal(expected, bytes);
        Assert.Equal(expected, layout.EntryLength(entry));
        Assert.Equal(RootArena.Handle, IndexLayout.IdentityOf(entry));
        Assert.Equal(layout.HasText ? 0 : expected, layout.FixedEntryBytes);
    }

    public static IEnumerable<TheoryDataRow<int, int>> Widths =>
    [
        // String parts, and bytes of fixed parts beside them.
        new(0, 8),
        new(1, 0),
        new(3, 0),
        new(3, 24),
        new(3, 40),
        new(4, 0),
        new(10, 0),
        new(251, 0),
    ];

    [Theory(Skip = SkipReason.BeforeSprint14)]
    [MemberData(nameof(Widths))]
    public void Node_takes_1_KiB_while_four_of_the_widest_entries_fit_and_the_smallest_size_class_that_holds_them_above(int texts, int fixedBytes)
    {
        var fields = new StringBuilder();
        var parts = new List<int>();

        for (var text = 0; text < texts; text++)
        {
            fields.Append($", {{ \"Id\": {100 + text}, \"Name\": \"Text{text}\", \"Type\": \"String\" }}");
            parts.Add(100 + text);
        }

        // Fixed parts of 16 and 8 bytes: Decimals and an Int64.
        for (var remaining = fixedBytes; remaining > 0; remaining -= remaining >= 16 ? 16 : 8)
        {
            var id = 1_000 + parts.Count;

            fields.Append($", {{ \"Id\": {id}, \"Name\": \"Fixed{id}\", \"Type\": \"{(remaining >= 16 ? "Decimal" : "Int64")}\" }}");
            parts.Add(id);
        }

        using var item = new RootArena(LatticeSchema.Compile(Json(fields.ToString()[2..], $"[{string.Join(", ", parts)}]")));
        var layout = IndexLayout.Compile(Index(item), item.Layout);

        // The widest entry, and the payload of a leaf of four of them and of an inner node of four
        // separators.
        var widest = IndexLayout.IdentityBytes + (texts * (1 + IndexLayout.TextCopyLimit)) + fixedBytes;
        var offsets = texts > 0 ? Offset : 0;
        var leaf = Header + (4 * (offsets + widest));
        var inner = Header + (5 * Child) + (4 * (offsets + widest));
        var needed = BlockHeader.Size + Math.Max(leaf, inner);
        var expected = needed <= IndexLayout.SmallestNodeBytes ? IndexLayout.SmallestNodeBytes : Arena.BlockSizeFor(needed - BlockHeader.Size);

        Assert.Equal(widest, layout.WidestEntryBytes);
        Assert.Equal(expected, layout.NodeBytes);
        Assert.Equal(expected - BlockHeader.Size, layout.PayloadBytes);
        Assert.True(layout.LeastSeparators >= IndexLayout.LeastEntriesPerNode);

        if (texts == 0 && fixedBytes <= 16)
        {
            Assert.Equal(IndexLayout.SmallestNodeBytes, layout.NodeBytes);
        }
    }

    // The declaration of the field Part of a row, and how its value is written.
    private static (string Declaration, Action<RootWriter, LatticeField> Value) PartOf(string part) => part switch
    {
        "Int32" => ("'Type': 'Int32'", (writer, field) => writer.SetInteger(field, -5)),
        "DateOnly" => ("'Type': 'DateOnly'", (writer, field) => writer.SetDateOnly(field, new DateOnly(2026, 11, 19))),
        "Int64" => ("'Type': 'Int64'", (writer, field) => writer.SetInteger(field, long.MinValue)),
        "Float64" => ("'Type': 'Float64'", (writer, field) => writer.SetFloat64(field, -0d)),
        "DateTimeOffset" => ("'Type': 'DateTimeOffset'", (writer, field) => writer.SetDateTimeOffset(field, DateTimeOffset.UnixEpoch)),
        "Guid" => ("'Type': 'Guid'", (writer, field) => writer.SetGuid(field, Guid.NewGuid())),
        "Decimal" => ("'Type': 'Decimal'", (writer, field) => writer.SetDecimal(field, 1.00m)),
        "nullable Int64" => ("'Type': 'Int64', 'Nullable': true", (writer, field) => writer.SetInteger(field, 7)),
        "text of 0 bytes" => ("'Type': 'String'", (writer, field) => writer.SetString(field, "")),
        "text of 8 bytes" => ("'Type': 'String'", (writer, field) => writer.SetString(field, "abcdefgh")),
        "text of 64 bytes" => ("'Type': 'String'", (writer, field) => writer.SetString(field, new string('a', 64))),
        "text of 65 bytes" => ("'Type': 'String'", (writer, field) => writer.SetString(field, new string('a', 65))),
        "text of 1,000 bytes" => ("'Type': 'String'", (writer, field) => writer.SetString(field, new string('a', 1_000))),
        "Dictionary text of 64 bytes" => ("'Type': 'String', 'StringStorage': 'Dictionary'", (writer, field) => writer.SetString(field, new string('d', 64))),
        "Dictionary text of 65 bytes" => ("'Type': 'String', 'StringStorage': 'Dictionary'", (writer, field) => writer.SetString(field, new string('d', 65))),
        _ => ("'Type': 'String', 'Nullable': true", (writer, field) => writer.SetNull(field)),
    };

    private static LatticeIndex Index(RootArena item)
    {
        Assert.True(item.EntityType.TryGetIndex("ByParts", out var index));

        return index;
    }

    private static LatticeSchema SchemaOf(string declaration, string parts) =>
        LatticeSchema.Compile(Json($"{{ \"Id\": 2, \"Name\": \"Part\", {declaration.Replace('\'', '"')} }}", parts));

    // Item, in a UTF-8 database: the key Id, an Int64, the fields given and the index ByParts over the
    // parts given.
    private static string Json(string fields, string parts) => $$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, {{fields}} ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByParts", "Fields": {{parts}} } ]
            }
          ]
        }
        """;
}
