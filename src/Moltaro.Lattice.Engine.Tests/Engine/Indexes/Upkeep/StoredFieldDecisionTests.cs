using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// What a replace does to an index is decided from the stored fields of its two roots, without building
// their entries (Indexes.SecondaryIndex.ChangeOf): the test of the stored fields has to decide exactly what
// the bytes of the two entries decide (IndexLayout.SameEntry against IndexLayout.WriteEntry), and the order
// of the two roots what the order of the two entries decides (IndexOrder.SameKey). The two are held against
// each other for every pair of four states of Mixed, which have each trap a test of the stored fields
// meets: a null with other bytes under it and one with zeros, a null against a zero, a Decimal of another
// scale, +0 against -0, NaNs of other bits, a DateTimeOffset with another offset at the same instant, a
// Plain text whose place in the root a longer Label moves, a text in another case under an Ordinal and
// under an OrdinalIgnoreCase comparison, a Dictionary text, and a long text shared through one text entry
// against another.
public sealed class StoredFieldDecisionTests
{
    private static readonly DateTimeOffset Instant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Test_of_the_stored_fields_decides_what_the_bytes_and_the_order_of_the_two_entries_decide()
    {
        using var item = new RootArena(Schema, "Mixed");
        var states = new[]
        {
            Write(item, number: 0, price: 1.0m, value: 0.0, at: Instant, label: "a", code: "code", region: "North", login: new string('l', 100)),
            WriteWithBytesUnderNull(item, price: 1.00m, value: -0.0, at: Instant.ToOffset(TimeSpan.FromHours(2)), label: new string('a', 40), code: "code", region: "NORTH", login: new string('l', 100)),
            Write(item, number: null, price: 2m, value: BitConverter.Int64BitsToDouble(0x7FF8000000000001), at: Instant.AddTicks(1), label: "a", code: "Code", region: "South", login: new string('m', 100)),
            Write(item, number: 0, price: 2.0m, value: BitConverter.Int64BitsToDouble(0x7FF8000000000002), at: Instant, label: "", code: "code", region: "North", login: new string('l', 100)),
        };
        var differ = new List<string>();
        var sameEntries = 0;

        foreach (var definition in item.EntityType.Indexes)
        {
            var layout = IndexLayout.Compile(definition, item.Layout);
            var order = new IndexOrder(layout, item.Arena);

            for (var left = 0; left < states.Length; left++)
            {
                for (var right = 0; right < states.Length; right++)
                {
                    var leftEntry = EntryOf(item, layout, states[left], RootArena.Handle);
                    var rightEntry = EntryOf(item, layout, states[right], RootArena.Handle);
                    var bytes = leftEntry.AsSpan().SequenceEqual(rightEntry);
                    var keys = order.SameKey(leftEntry, rightEntry);
                    var fields = layout.SameEntry(item.Reader(states[left]), item.Reader(states[right]));
                    var fieldKeys = order.SameKey(item.Reader(states[left]), item.Reader(states[right]));

                    if (bytes != fields || keys != fieldKeys)
                    {
                        differ.Add($"{definition.Name}, states {left} and {right}: the same bytes {bytes} against {fields}, the same key {keys} against {fieldKeys}");
                    }

                    if (bytes && left != right)
                    {
                        sameEntries++;
                    }
                }
            }
        }

        Assert.Empty(differ);

        // The premise: some pairs of different states contribute the same entry, so the grid holds both
        // answers.
        Assert.True(sameEntries > 0, "No two different states contribute the same entry to an index.");
    }

    // A state of Mixed under the handle of the tests.
    private static ArenaAddress Write(RootArena item, long? number, decimal price, double value, DateTimeOffset at, string label, string code, string region, string login)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), 1);

        if (number is { } some)
        {
            writer.SetInteger(item.Field("Number"), some);
        }
        else
        {
            writer.SetNull(item.Field("Number"));
        }

        writer.SetDecimal(item.Field("Price"), price);
        writer.SetFloat64(item.Field("Value"), value);
        writer.SetDateTimeOffset(item.Field("At"), at);
        writer.SetString(item.Field("Label"), label);
        writer.SetString(item.Field("Code"), code);
        writer.SetString(item.Field("Region"), region);
        writer.SetString(item.Field("Login"), login);

        return writer.Complete();
    }

    // A state whose Number is null with bytes under the null that are not zero, as a root a child received
    // may hold them.
    private static ArenaAddress WriteWithBytesUnderNull(RootArena item, decimal price, double value, DateTimeOffset at, string label, string code, string region, string login)
    {
        var root = Write(item, null, price, value, at, label, code, region, login);
        ref readonly var stored = ref item.Layout.StoredFieldOf(item.Field("Number"));

        item.Arena.Payload(root).Slice(stored.Offset - BlockHeader.Size, stored.Width).Fill(0xAB);

        return root;
    }

    // Mixed: the key Id and a field of each trap, each in an index of its own but Label, declared before
    // Code so that its length decides where Code lies in the root.
    private static LatticeSchema Schema { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 30, "Name": "Mixed",
          "Fields": [
            { "Id": 1, "Name": "Id", "Type": "Int64" },
            { "Id": 2, "Name": "Number", "Type": "Int64", "Nullable": true },
            { "Id": 3, "Name": "Price", "Type": "Decimal" },
            { "Id": 4, "Name": "Value", "Type": "Float64" },
            { "Id": 5, "Name": "At", "Type": "DateTimeOffset" },
            { "Id": 6, "Name": "Label", "Type": "String" },
            { "Id": 7, "Name": "Code", "Type": "String" },
            { "Id": 8, "Name": "Region", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
            { "Id": 9, "Name": "Login", "Type": "String" }
          ],
          "PrimaryKey": [1],
          "Indexes": [
            { "Id": 1, "Name": "ByNumber", "Fields": [2] },
            { "Id": 2, "Name": "ByPrice", "Fields": [3] },
            { "Id": 3, "Name": "ByValue", "Fields": [4] },
            { "Id": 4, "Name": "ByAt", "Fields": [5] },
            { "Id": 5, "Name": "ByCode", "Fields": [7] },
            { "Id": 6, "Name": "ByRegion", "Fields": [8] },
            { "Id": 7, "Name": "ByLogin", "Fields": [9] },
            { "Id": 8, "Name": "ByAll", "Fields": [2, 3, 4, 5, 7, 8, 9] }
          ]
        },
        """);
}
