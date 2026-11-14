namespace Moltaro.Lattice.Tests;

// A record holds a List as a List<T>, a Map as a Dictionary<K, V> with the comparer of its key
// declaration and a Set as a HashSet<T> with the comparer of its element declaration. It
// accepts any sequence of the right type and copies it, so that later changes of the input do
// not reach the record. Input is checked when it is set: an element, a key or a value of
// another type, or null where its declaration does not allow it, is refused; a List keeps the
// order and the duplicates of its input, a key given twice in the input of a Map is refused,
// and of equal members of a Set the first is kept.
public sealed class RecordCollectionTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void List_keeps_the_order_and_the_duplicates_of_its_input_in_a_copy()
    {
        var record = NewRecord();
        var scores = new List<int?> { 3, null, 1, 3 };

        record["Scores"] = scores;
        scores.Add(9);

        var held = Assert.IsType<List<int?>>(record["Scores"]);
        Assert.Equal([3, null, 1, 3], held);
        Assert.NotSame(scores, held);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void List_accepts_any_sequence_of_its_element_type()
    {
        var record = NewRecord();

        record["Scores"] = new int?[] { 1, 2 };
        Assert.Equal([1, 2], Assert.IsType<List<int?>>(record["Scores"]));

        record["Scores"] = Enumerable.Range(1, 3).Select(number => (int?)number);
        Assert.Equal([1, 2, 3], Assert.IsType<List<int?>>(record["Scores"]));

        // An empty List is a value, not null.
        record["Scores"] = Array.Empty<int?>();
        Assert.Empty(Assert.IsType<List<int?>>(record["Scores"]));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void List_refuses_an_element_of_another_type_and_a_null_its_element_does_not_allow()
    {
        var record = NewRecord();

        Assert.Throws<ArgumentException>(() => record["Scores"] = new[] { "one", "two" });
        Assert.Throws<ArgumentException>(() => record["Scores"] = 1);

        // The elements of Blobs are not nullable, those of Scores are.
        Assert.Throws<ArgumentException>(() => record["Blobs"] = new byte[]?[] { [1, 2], null });
        record["Scores"] = new int?[] { null };
        Assert.Equal([null], Assert.IsType<List<int?>>(record["Scores"]));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Map_holds_a_copy_of_its_input_with_the_comparer_of_its_key_declaration()
    {
        var record = NewRecord();
        var options = new Dictionary<string, string?> { ["Theme"] = "dark", ["Mode"] = null };
        var members = new Dictionary<string, List<Guid>>(StringComparer.Ordinal) { ["Main"] = [Guid.Empty] };

        record["Options"] = options;
        record["Members"] = members;
        options["Theme"] = "light";

        var heldOptions = Assert.IsType<Dictionary<string, string>>(record["Options"]);
        Assert.NotSame(options, heldOptions);
        Assert.Equal("dark", heldOptions["Theme"]);
        Assert.Null(heldOptions["Mode"]);

        // Options compares its keys as Ordinal, Members as OrdinalIgnoreCase, whatever the
        // comparer of the input.
        Assert.False(heldOptions.ContainsKey("theme"));
        var heldMembers = Assert.IsType<Dictionary<string, List<Guid>>>(record["Members"]);
        Assert.True(heldMembers.ContainsKey("MAIN"));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Map_accepts_any_sequence_of_key_value_pairs()
    {
        var record = NewRecord();

        record["PaintCounts"] = new List<KeyValuePair<byte, int>> { new(0, 3), new(1, 4) };

        var held = Assert.IsType<Dictionary<byte, int>>(record["PaintCounts"]);
        Assert.Equal(3, held[0]);
        Assert.Equal(4, held[1]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Key_given_twice_in_the_input_of_a_Map_is_refused()
    {
        var record = NewRecord();

        // Even with the same value: the input does not say which entry is meant.
        Assert.Throws<ArgumentException>(() => record["PaintCounts"] = new[] { KeyValuePair.Create((byte)0, 3), KeyValuePair.Create((byte)0, 3) });

        // Two keys that the key declaration holds equal are one key given twice.
        var members = new Dictionary<string, List<Guid>>(StringComparer.Ordinal) { ["Main"] = [], ["main"] = [] };
        Assert.Throws<ArgumentException>(() => record["Members"] = members);

        // The same two keys under Ordinal are two keys.
        record["Options"] = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Main"] = "1", ["main"] = "2" };
        Assert.Equal(2, Assert.IsType<Dictionary<string, string>>(record["Options"]).Count);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Map_refuses_a_key_or_a_value_of_another_type_and_a_null_its_declaration_does_not_allow()
    {
        var record = NewRecord();

        Assert.Throws<ArgumentException>(() => record["PaintCounts"] = new Dictionary<string, int> { ["Red"] = 3 });
        Assert.Throws<ArgumentException>(() => record["PaintCounts"] = new Dictionary<byte, string> { [0] = "three" });
        Assert.Throws<ArgumentException>(() => record["Options"] = new[] { "Theme" });

        // A key is never null; a value is null only where it is nullable.
        Assert.Throws<ArgumentException>(() => record["Options"] = new[] { KeyValuePair.Create<string, string?>(null!, "dark") });
        Assert.Throws<ArgumentException>(() => record["Members"] = new Dictionary<string, List<Guid>?> { ["Main"] = null });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Set_keeps_the_first_of_equal_members_in_a_copy_with_the_comparer_of_its_element_declaration()
    {
        var record = NewRecord();
        var aliases = new List<string?> { "Main", "main", null, "Other", "MAIN", null };

        record["Aliases"] = aliases;
        aliases.Add("Third");

        var held = Assert.IsType<HashSet<string?>>(record["Aliases"]);
        Assert.Equal(3, held.Count);

        // The first spelling is kept, and the members compare as OrdinalIgnoreCase.
        Assert.True(held.TryGetValue("MAIN", out var kept));
        Assert.Equal("Main", kept);
        Assert.Contains("other", held);
        Assert.DoesNotContain("Third", held);

        // A nullable element allows one null member.
        Assert.Contains(null, held);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Set_compares_its_members_as_its_element_declaration_does_and_not_as_its_input()
    {
        var record = NewRecord();

        // Rights compares as Ordinal: two spellings are two members.
        record["Rights"] = new[] { "read", "READ" };
        Assert.Equal(2, Assert.IsType<HashSet<string>>(record["Rights"]).Count);

        // The comparer of the input is not taken over.
        record["Rights"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read" };
        Assert.DoesNotContain("READ", Assert.IsType<HashSet<string>>(record["Rights"]));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Set_refuses_a_member_of_another_type_and_a_null_its_element_does_not_allow()
    {
        var record = NewRecord();

        Assert.Throws<ArgumentException>(() => record["Teams"] = new[] { "group" });
        Assert.Throws<ArgumentException>(() => record["Rights"] = new[] { "read", null });

        // The Set itself is nullable, and empty is not null.
        record["Teams"] = null;
        Assert.Null(record["Teams"]);
        record["Teams"] = Array.Empty<Guid>();
        Assert.Empty(Assert.IsType<HashSet<Guid>>(record["Teams"]));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Collections_inside_collections_are_held_and_checked_at_every_position()
    {
        var record = NewRecord();
        var group = Guid.NewGuid();

        record["Layers"] = new List<List<long>> { new() { 1, 2 }, new() { 2 } };
        record["Grid"] = new Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>>
        {
            [group] = new() { [1] = [new DateOnly(2026, 11, 14)] },
        };

        Assert.Equal([1L, 2L], Assert.IsType<List<List<long>>>(record["Layers"])[0]);
        var grid = Assert.IsType<Dictionary<Guid, Dictionary<int, HashSet<DateOnly>>>>(record["Grid"]);
        Assert.Contains(new DateOnly(2026, 11, 14), grid[group][1]);

        // An inner collection of another element type, and a null inner collection.
        Assert.Throws<ArgumentException>(() => record["Layers"] = new List<List<string>> { new() { "one" } });
        Assert.Throws<ArgumentException>(() => record["Layers"] = new List<List<long>?> { new() { 1 }, null });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Elements_of_an_enum_type_and_of_Binary_are_held_as_their_values()
    {
        var record = NewRecord();

        // An enum element holds the underlying integer of its enum type.
        record["Paints"] = new byte[] { 1, 0, 1 };
        Assert.Equal(new byte[] { 1, 0, 1 }, Assert.IsType<List<byte>>(record["Paints"]));

        record["Blobs"] = new[] { new byte[] { 7 } };
        Assert.Equal(7, Assert.Single(Assert.IsType<List<byte[]>>(record["Blobs"]))[0]);
    }

    private static LatticeRecord NewRecord() => new(ValueSchema.User(ValueSchema.Compile()));
}
