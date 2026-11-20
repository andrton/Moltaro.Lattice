using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Order;

// A step of a search by a full key compares in three ways, and the three agree, part by part, with the
// order of the entries (docs/design/ordered-index.md, "Entries and their order": a null equals another null,
// so a composite key goes on to its next part, and the check of an entry against its root is that same
// order being zero; "An entry is a hint": a reader never finds an entity under a key its root no longer
// has, and an entity that has the key for the whole search is found):
// - the order of a search key against an entry, by which the step descends (IndexOrder.Compare), has the
//   sign of the order of the entry of a root with that key against the entry, and is zero exactly when
//   the two keys are equal;
// - whether an entry has the key, which decides whether the entry a step finds is of the key, and where
//   the run of the key ends (IndexOrder.SameKey);
// - whether a root has the key, by which the step passes over an entry whose root no longer has it
//   (IndexOrder.HasKey).
// Every key is held against every root the parts of an index of Composite can make: ByNumbers (First,
// Second, Third), three nullable Int64; ByNumberAndLabel (First, Label), a String after a fixed part;
// ByLabelAndNumber (Label, Second), a fixed part after a String. A number is null, 0 or 7 and a text null,
// empty or "north", so the grid holds a null in each position, a fixed and a text part after a null fixed
// part, a fixed part after a null text part, and a root whose key differs from the searched key only in a
// part after a null: an entry of the key whose root has moved on, which the step must pass over. Zero is
// among the numbers, so that bytes read under a null in place of a value are seen.
public sealed class CompositeKeyComparisonTests
{
    private static readonly object?[] Numbers = [null, 0L, 7L];

    private static readonly object?[] Labels = [null, "", "north"];

    [Fact]
    public void Order_of_a_key_the_end_of_its_run_and_the_check_of_a_root_agree_on_every_key_of_a_composite_index()
    {
        using var item = new RootArena(Schema, "Composite");
        Span<byte> scratch = stackalloc byte[SearchKey.ScratchBytes];
        var differ = new List<string>();

        foreach (var definition in item.EntityType.Indexes)
        {
            var layout = IndexLayout.Compile(definition, item.Layout);
            var order = new IndexOrder(layout, item.Arena);
            var states = StatesOf(definition);
            var roots = states.Select(state => Write(item, definition, state)).ToList();
            var entries = roots.Select(root => EntryOf(item, layout, root, RootArena.Handle)).ToList();

            for (var searched = 0; searched < states.Count; searched++)
            {
                var key = KeyOf(states[searched]);

                for (var other = 0; other < states.Count; other++)
                {
                    var probe = new SearchKey(layout, key, RootArena.Handle, scratch);
                    var equal = states[searched].SequenceEqual(states[other]);
                    var entryOrder = Math.Sign(order.Compare(entries[searched], entries[other]));
                    var keyOrder = Math.Sign(order.Compare(probe, entries[other]));
                    var descent = keyOrder == 0;
                    var run = order.SameKey(probe, entries[other]);
                    var root = order.HasKey(item.Reader(roots[other]), probe);

                    if ((entryOrder == 0) != equal || keyOrder != entryOrder || descent != equal || run != equal || root != equal)
                    {
                        differ.Add($"{definition.Name}, the key {Shown(states[searched])} against {Shown(states[other])}, equal {equal}: the order of the entries {entryOrder} and of the key {keyOrder}; the same key by the descent {descent}, by the end of the run {run}, by the root {root}");
                    }
                }
            }
        }

        Assert.Empty(differ);
    }

    // Every combination of the values of the parts of the index, in the order of its parts.
    private static List<object?[]> StatesOf(LatticeIndex definition)
    {
        IEnumerable<object?[]> states = [[]];

        foreach (var part in definition.Parts)
        {
            var values = part.Field.Declaration.Type == LatticeFieldTypeEnum.String ? Labels : Numbers;

            states = states.SelectMany(state => values.Select(value => (object?[])[.. state, value]));
        }

        return [.. states];
    }

    // A root of Composite whose parts of the index have the values of the state; every other field is null.
    private static ArenaAddress Write(RootArena item, LatticeIndex definition, object?[] state)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), 1);

        for (var place = 0; place < state.Length; place++)
        {
            var field = definition.Parts[place].Field;

            switch (state[place])
            {
                case null:
                    writer.SetNull(field);
                    break;
                case long number:
                    writer.SetInteger(field, number);
                    break;
                case string text:
                    writer.SetString(field, text);
                    break;
            }
        }

        return writer.Complete();
    }

    // The key of the state, each value given as an object, which a key takes as the value it holds.
    private static IndexKey KeyOf(object?[] state) => state.Length == 2
        ? IndexKey.Equal(state[0], state[1])
        : IndexKey.Equal(state[0], state[1], state[2]);

    private static string Shown(object?[] state) =>
        $"({string.Join(", ", state.Select(value => value switch { null => "null", string text => $"\"{text}\"", _ => value.ToString() }))})";

    private static LatticeSchema Schema { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Composite",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "First", "Type": "Int64", "Nullable": true },
                { "Id": 3, "Name": "Second", "Type": "Int64", "Nullable": true },
                { "Id": 4, "Name": "Third", "Type": "Int64", "Nullable": true },
                { "Id": 5, "Name": "Label", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByNumbers", "Fields": [2, 3, 4] },
                { "Id": 2, "Name": "ByNumberAndLabel", "Fields": [2, 5] },
                { "Id": 3, "Name": "ByLabelAndNumber", "Fields": [5, 3] }
              ]
            }
          ]
        }
        """);
}
