using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// A value that a prepared, unpublished insert claims is compared with the value a later write wants by the
// comparison of every part, as a value of the published tree is (decision on unique indexes, "Equality
// follows every component's schema comparator"; WriteCoordinator, "A value of a unique index"): a text
// without case in another case, also one longer than a piece of the comparison with a letter of a
// supplementary plane where the piece ends, stored in the Dictionary; a NaN of other bits and the other zero;
// a Decimal of another scale; a composite key equal in every part. Such a write waits for the claim and is
// refused once it is published; a write of a value the comparison holds different, and of a composite key
// with a null part, does not wait and succeeds. A prepared replace that gives a composite key a null part,
// or takes a text to another case of it, is compared the same way: the first gives the value up, and a write
// of it waits and takes it; the second keeps it, and a write of it is refused at once. The persistence step
// of the test (HeldPersistence) holds the claims. Claimed, below, by name; every wait is bounded.
public sealed class ClaimedValueEqualityTests
{
    [Fact]
    public async Task Write_of_a_value_equal_by_the_comparison_to_a_prepared_claim_waits_and_is_refused_and_one_held_different_succeeds()
    {
        var storage = new HeldPersistence();
        var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs { Persistence = storage });
        var claimed = database.Set("Claimed");
        var longText = new string('a', 255) + "\U00010428" + new string('b', 44);
        var otherNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001));

        try
        {
            var claims = new List<Task<LatticeWriteResult>>
            {
                Write(claimed, 1, ("Folded", "Ada")),
            };

            await storage.NextAsync();

            claims.Add(Write(claimed, 2, ("Long", longText)));
            claims.Add(Write(claimed, 3, ("Real", double.NaN)));
            claims.Add(Write(claimed, 4, ("Zero", -0.0)));
            claims.Add(Write(claimed, 5, ("Amount", 1.0m)));
            claims.Add(Write(claimed, 6, ("A", "a"), ("B", 1)));
            claims.Add(Write(claimed, 7, ("A", "n")));

            var equal = new List<(string Index, Task<LatticeWriteResult> Write)>
            {
                ("ByFolded", Write(claimed, 11, ("Folded", "ADA"))),
                ("ByLong", Write(claimed, 12, ("Long", longText.Replace("\U00010428", "\U00010400", StringComparison.Ordinal)))),
                ("ByReal", Write(claimed, 13, ("Real", otherNaN))),
                ("ByZero", Write(claimed, 14, ("Zero", 0.0))),
                ("ByAmount", Write(claimed, 15, ("Amount", 1.00m))),
                ("ByAB", Write(claimed, 16, ("A", "a"), ("B", 1))),
            };

            var different = new List<Task<LatticeWriteResult>>
            {
                Write(claimed, 21, ("Folded", "Adb")),
                Write(claimed, 22, ("Long", longText.Replace("\U00010428", "\U00010429", StringComparison.Ordinal))),
                Write(claimed, 23, ("Real", double.PositiveInfinity)),
                Write(claimed, 24, ("Zero", double.Epsilon)),
                Write(claimed, 25, ("Amount", 1.0000000000000000000000000001m)),
                Write(claimed, 26, ("A", "a"), ("B", 2)),

                // A key with a null part is exempt: it neither waits for the claim of ("n", null) nor is refused.
                Write(claimed, 27, ("A", "n")),
            };

            Assert.DoesNotContain(equal, write => write.Write.IsCompleted);

            storage.ReleaseAll();

            foreach (var claim in claims)
            {
                Assert.True((await claim.WaitAsync(Bound)).Changed);
            }

            foreach (var (index, write) in equal)
            {
                var refused = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
                Assert.Equal(index, refused.IndexName);
            }

            foreach (var write in different)
            {
                Assert.True((await write.WaitAsync(Bound)).Changed);
            }

            Assert.Equal([1L], IdsOf(claimed, "ByFolded", IndexKey.Equal("aDa")));
            Assert.Equal([2L], IdsOf(claimed, "ByLong", IndexKey.Equal(longText.ToUpperInvariant())));
            Assert.Equal([3L], IdsOf(claimed, "ByReal", IndexKey.Equal(otherNaN)));
            Assert.Equal([4L], IdsOf(claimed, "ByZero", IndexKey.Equal(0.0)));
            Assert.Equal([5L], IdsOf(claimed, "ByAmount", IndexKey.Equal(1m)));
            Assert.Equal([6L], IdsOf(claimed, "ByAB", IndexKey.Equal("a", 1)));
            Assert.Equal([7L, 27L], IdsOf(claimed, "ByAB", IndexKey.Equal<string, int?>("n", null)));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Prepared_replace_that_gives_a_key_a_null_part_frees_the_value_and_one_that_changes_only_its_case_keeps_it()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs { Persistence = storage });
        var claimed = database.Set("Claimed");

        try
        {
            await Write(claimed, 1, ("A", "a"), ("B", 1), ("Folded", "Ada"));
            storage.Hold();

            var replace = Replace(claimed, 1, ("A", "a"), ("B", null), ("Folded", "ADA"));
            await storage.NextAsync();

            var freed = Write(claimed, 2, ("A", "a"), ("B", 1));

            // Refused while the persistence step still holds the replace, which keeps the text in another case.
            var kept = await Assert.ThrowsAsync<LatticeException>(() => Write(claimed, 3, ("Folded", "ada")).WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, kept.Error);
            Assert.Equal("ByFolded", kept.IndexName);
            Assert.False(freed.IsCompleted, "The insert of the freed key completed while the replace was held.");
            Assert.Equal(2, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await freed.WaitAsync(Bound)).Changed);
            Assert.Equal([2L], IdsOf(claimed, "ByAB", IndexKey.Equal("a", 1)));
            Assert.Equal([1L], IdsOf(claimed, "ByAB", IndexKey.Equal<string, int?>("a", null)));
            Assert.Equal([1L], IdsOf(claimed, "ByFolded", IndexKey.Equal("ada")));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Claimed, by name: Id, an Int64; Folded, a String compared without case; Long, one compared without case
    // with Dictionary storage; Real and Zero, Float64s; Amount, a Decimal; each alone in a unique index named
    // "By" and its name; A, a String, and B, an Int32, in the unique index ByAB. Every field but Id can be null.
    private static LatticeSchema Schema { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Claimed",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Folded", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase" },
                { "Id": 3, "Name": "Long", "Type": "String", "Nullable": true, "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Real", "Type": "Float64", "Nullable": true },
                { "Id": 5, "Name": "Zero", "Type": "Float64", "Nullable": true },
                { "Id": 6, "Name": "Amount", "Type": "Decimal", "Nullable": true },
                { "Id": 7, "Name": "A", "Type": "String", "Nullable": true },
                { "Id": 8, "Name": "B", "Type": "Int32", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByFolded", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByLong", "Fields": [3], "Unique": true },
                { "Id": 3, "Name": "ByReal", "Fields": [4], "Unique": true },
                { "Id": 4, "Name": "ByZero", "Fields": [5], "Unique": true },
                { "Id": 5, "Name": "ByAmount", "Fields": [6], "Unique": true },
                { "Id": 6, "Name": "ByAB", "Fields": [7, 8], "Unique": true }
              ]
            }
          ]
        }
        """);

    // An insert of the record of the id with the values given by field; every other field is null.
    private static Task<LatticeWriteResult> Write(ILatticeSet set, long id, params (string Field, object? Value)[] values) =>
        set.InsertAsync(RecordOf(set, id, values)).AsTask();

    // A replace of the record of the id with the values given by field; every other field is null.
    private static Task<LatticeWriteResult> Replace(ILatticeSet set, long id, params (string Field, object? Value)[] values) =>
        set.ReplaceAsync(RecordOf(set, id, values)).AsTask();

    private static LatticeRecord RecordOf(ILatticeSet set, long id, (string Field, object? Value)[] values)
    {
        var record = new LatticeRecord(set.EntityType) { ["Id"] = id };

        foreach (var (field, value) in values)
        {
            record[field] = value;
        }

        return record;
    }

    // The ids of the records a search of the index for the key finds, in ascending order.
    private static long[] IdsOf(ILatticeSet set, string index, IndexKey key) =>
        [.. set.Index(index).Find(key).Select(record => record.Get<long>("Id")).Order()];
}
