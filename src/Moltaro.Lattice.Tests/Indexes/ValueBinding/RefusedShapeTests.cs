namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A key or a bound whose shape the index cannot serve is refused before anything is searched, with the
// ArgumentException of ILatticeIndex, through the class and by name, by Find and by FindAsync (the
// Acceptance of S15: an unsupported key or bound shape is refused; IndexKey: a value of a type no scalar
// field has is kept as an object, to be refused when the key is used):
// - a value of a type no scalar field has, each beside the field whose type it is nearest to: an object
//   of a class, a tuple, a char for a String, an Int128 for an Int64, a Half for a Float32; as the value
//   and as an inclusive lower and an exclusive upper bound;
// - a range of which one bound has a type the allowlist refuses while the other bound is good, either way
//   round.
// IndexKeyArgumentTests holds a value of a type no field has, a key with more values than the index has
// fields, a null for a part that is never null, the default of IndexBound and of IndexKey;
// IndexPartTypeTests a part that ends at an owned value or a collection, and the name of an Enum member.
public sealed class RefusedShapeTests
{
    [Fact]
    public async Task Value_of_a_type_no_scalar_field_has_is_refused_as_a_value_and_as_a_bound()
    {
        await using var database = await BindingSchema.OpenAsync();
        var item = new Item();

        await ExpectRefusedInEveryFormAsync(database, "ByInt32", item, "an object of a class");
        await ExpectRefusedInEveryFormAsync(database, "ByInt32", (1, 2), "a tuple");
        await ExpectRefusedInEveryFormAsync(database, "ByExact", 'a', "a char");
        await ExpectRefusedInEveryFormAsync(database, "ByInt64", (Int128)1, "an Int128");
        await ExpectRefusedInEveryFormAsync(database, "ByFloat32", (Half)1, "a Half");
    }

    [Fact]
    public async Task Range_with_one_bound_of_a_type_the_allowlist_refuses_is_refused_although_the_other_bound_is_good()
    {
        await using var database = await BindingSchema.OpenAsync();

        await BindingSearch.ExpectRefusedAsync(database, "ByInt32", IndexKey.All.Range(IndexBound.Inclusive(1), IndexBound.Exclusive(2.5)), "ByInt32 from 1 to 2.5");
        await BindingSearch.ExpectRefusedAsync(database, "ByInt32", IndexKey.All.Range(IndexBound.Inclusive(1.0), IndexBound.Exclusive(2)), "ByInt32 from 1.0 to 2");
        await BindingSearch.ExpectRefusedAsync(database, "ByDecimal", IndexKey.All.Range(IndexBound.Inclusive(1m), IndexBound.Inclusive(2.0)), "ByDecimal from 1m to 2.0");
        await BindingSearch.ExpectRefusedAsync(database, "ByFloat64", IndexKey.All.Range(IndexBound.Inclusive(1m), IndexBound.Inclusive(2.0)), "ByFloat64 from 1m to 2.0");
        await BindingSearch.ExpectRefusedAsync(database, "ByExact", IndexKey.All.Range(IndexBound.Inclusive("a"), IndexBound.Exclusive('b')), "ByExact from \"a\" to 'b'");
    }

    private static async Task ExpectRefusedInEveryFormAsync<T>(ILatticeDatabase database, string index, T value, string shape)
        where T : notnull
    {
        await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.Equal(value), $"{index} = {shape}");
        await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(IndexBound.Inclusive(value), null), $"{index} from {shape}");
        await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(null, IndexBound.Exclusive(value)), $"{index} to {shape}");
    }
}
