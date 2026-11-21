namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A null that has a type, a Nullable without a value or a null string, is a value of a part only where a
// value of that type could be, by the allowlist, and only where the part can be null; a null given as an
// object has no type and follows the part alone (IndexKey; docs/decisions/queries.md, Q07.3.6: for nullable
// inputs compatibility is still checked by the declared type, and a typed null does not make an otherwise
// incompatible type valid; Q54 of docs/work/open-questions.md). The fields of Item (BindingSchema): the
// nullable Int32, Decimal, Float32, Float64 and Exact, a String; Count, an Int32 that is never null; and
// ByTriple over Int32, Exact and Decimal.
// - A null that binds finds the entities without a value in the part, through the class and by name, by
//   Find and by FindAsync; one that is refused is the ArgumentException of a key the index cannot serve.
// - In ByTriple a null stands in every position, given with the type of its part and as an object, and
//   finds the one entity with that null.
// IndexKeyArgumentTests holds a null for a part that is never null; ExactSearchTests a null part before a
// part with a value.
public sealed class TypedNullTests
{
    [Fact]
    public async Task Null_of_a_type_the_part_accepts_or_given_as_an_object_finds_the_entities_without_a_value()
    {
        (string Field, IndexKey Key, string Null)[] nulls =
        [
            ("Int32", IndexKey.Equal<long?>(null), "(long?)null"),
            ("Decimal", IndexKey.Equal<int?>(null), "(int?)null"),
            ("Float32", IndexKey.Equal<int?>(null), "(int?)null"),
            ("Float64", IndexKey.Equal<int?>(null), "(int?)null"),
            ("Exact", IndexKey.Equal<string?>(null), "(string?)null"),
            ("Int32", IndexKey.Equal<object?>(null), "(object?)null"),
            ("Decimal", IndexKey.Equal<object?>(null), "(object?)null"),
            ("Float32", IndexKey.Equal<object?>(null), "(object?)null"),
            ("Float64", IndexKey.Equal<object?>(null), "(object?)null"),
            ("Exact", IndexKey.Equal<object?>(null), "(object?)null"),
        ];

        foreach (var (field, key, given) in nulls)
        {
            // A database of its own for each field: an entity of another field would have no value here.
            await using var database = await BindingSchema.OpenAsync();
            await BindingSearch.InsertAsync(database, field, [new Stored(1, ValueOf(field)), new Stored(2, null)]);

            await BindingSearch.ExpectAsync(database, field, key, reverse: false, [new Stored(2, null)], $"By{field} = {given}");
        }
    }

    [Fact]
    public async Task Null_of_a_type_the_part_does_not_accept_is_refused()
    {
        await using var database = await BindingSchema.OpenAsync();

        await BindingSearch.ExpectRefusedAsync(database, "ByInt32", IndexKey.Equal<double?>(null), "ByInt32 = (double?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByInt32", IndexKey.Equal<string?>(null), "ByInt32 = (string?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByExact", IndexKey.Equal<int?>(null), "ByExact = (int?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByDecimal", IndexKey.Equal<double?>(null), "ByDecimal = (double?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByTriple", IndexKey.Equal<int?, int?, decimal?>(1, null, 1m), "ByTriple = (1, (int?)null, 1)");
    }

    [Fact]
    public async Task Null_given_as_a_Nullable_of_a_type_no_field_has_is_refused_for_every_part_and_a_boxed_null_has_no_type()
    {
        await using var database = await BindingSchema.OpenAsync();
        await BindingSearch.InsertAsync(database, "Int32", [new Stored(1, null)]);

        // No value of these types is a value of any part, so neither is their null.
        foreach (var index in new[] { "ByInt32", "ByLevel", "ByDateTimeOffset", "ByExact" })
        {
            await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.Equal<DateTime?>(null), $"{index} = (DateTime?)null");
            await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.Equal<DotNetLevelEnum?>(null), $"{index} = (DotNetLevelEnum?)null");
            await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.Equal<char?>(null), $"{index} = (char?)null");
        }

        // A Nullable without a value given as an object is boxed to a null reference, which has no type.
        await BindingSearch.ExpectAsync(database, "Int32", IndexKey.Equal<object?>((double?)null), reverse: false, [new Stored(1, null)], "ByInt32 = (object?)(double?)null");
    }

    [Fact]
    public async Task Null_of_any_type_and_as_an_object_is_refused_for_a_part_that_is_never_null()
    {
        await using var database = await BindingSchema.OpenAsync();

        await BindingSearch.ExpectRefusedAsync(database, "ByCount", IndexKey.Equal<double?>(null), "ByCount = (double?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByCount", IndexKey.Equal<long?>(null), "ByCount = (long?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByCount", IndexKey.Equal<int?>(null), "ByCount = (int?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByCount", IndexKey.Equal<string?>(null), "ByCount = (string?)null");
        await BindingSearch.ExpectRefusedAsync(database, "ByCount", IndexKey.Equal<object?>(null), "ByCount = (object?)null");
    }

    [Fact]
    public async Task Null_in_every_position_of_a_composite_key_typed_and_as_an_object_finds_the_entity_with_that_null()
    {
        await using var database = await BindingSchema.OpenAsync();
        var items = database.Set<Item>();
        var records = database.Set("Item");

        // Every combination of a value and a null in the three parts, the id its three bits.
        for (var combination = 0; combination < 8; combination++)
        {
            var item = new Item
            {
                Id = combination,
                Int32 = (combination & 1) == 0 ? 7 : null,
                Exact = (combination & 2) == 0 ? "seven" : null,
                Decimal = (combination & 4) == 0 ? 7.00m : null,
            };

            await items.InsertAsync(item);
        }

        for (var combination = 0; combination < 8; combination++)
        {
            int? first = (combination & 1) == 0 ? 7 : null;
            var second = (combination & 2) == 0 ? "seven" : null;
            decimal? third = (combination & 4) == 0 ? 7m : null;

            var typed = IndexKey.Equal(first, second, third);
            var untyped = IndexKey.Equal<object?, object?, object?>(first, second, third);

            foreach (var key in new[] { typed, untyped })
            {
                Assert.Equal(combination, Assert.Single(items.Index("ByTriple").Find(key)).Id);
                Assert.Equal((long)combination, Assert.Single(records.Index("ByTriple").Find(key)).Get<long>("Id"));
                Assert.Equal(combination, Assert.Single((await items.Index("ByTriple").FindAsync(key, new LatticeFindOptions())).Items).Id);
                Assert.Equal((long)combination, Assert.Single((await records.Index("ByTriple").FindAsync(key, new LatticeFindOptions())).Items).Get<long>("Id"));
            }
        }
    }

    private static object ValueOf(string field) => field switch
    {
        "Int32" => 7,
        "Decimal" => 7m,
        "Float32" => 7f,
        "Float64" => 7d,
        _ => "seven",
    };
}
