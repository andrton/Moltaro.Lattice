using Moltaro.Lattice.Tests.Indexes.Walk;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// An Enum part of an index is ordered as the number of its underlying type and given as that integer, and
// only an integer its enum type allows is a value of it (LatticeEnumType: values compare as numbers of the
// underlying type, with its sign, and IsValid says which may be stored; IndexKey; the milestone contract,
// "Index preparation and traversal": invalid enum and Flags bits are rejected for lookup and bound
// parameters alike; IndexKey: a .NET enum value is not accepted). The fields of Item (BindingSchema):
// - Level, over Int16 with the members -300, -2, 5 and 300; Wide, over UInt64 with 1, 2^63 + 5 and the
//   largest UInt64, so that a member lies above the largest Int64; Rights, the Flags type over Int32 with
//   Read 1, Write 2, Delete 4 and Edit 3.
// - The whole index and a range, in both directions, is in the numeric order of the oracle: negative
//   members first for Level, the members above the largest Int64 last for Wide, a combination by its whole
//   mask for Rights.
// - An integer the enum type does not declare is refused as a value and as an inclusive and an exclusive
//   bound, lower and upper; for Rights an integer with a bit no member declares. A combination of
//   declared bits that no member names is a value of Rights, and so is zero exactly when IsValid says so.
// - An integer of another width that fits the underlying type is the same value; a value of a .NET enum,
//   even one with the members of the enum type, is refused.
// IndexPartTypeTests holds an Enum part given as its underlying integer and refused as the name of a
// member; IndexPartValueTests an Enum part found by its value and by null.
public sealed class EnumAndFlagsTests
{
    [Fact]
    public async Task Enum_and_Flags_parts_are_in_numeric_order_in_the_whole_index_and_in_a_range_in_both_directions()
    {
        await ExpectOrderAsync(
            "Level",
            BindingSearch.Numbered((short)300, (short)-2, (short)5, (short)-300, (short)-2, null),
            (RangeEnd.Including((short)-300), RangeEnd.Excluding((short)300)),
            (RangeEnd.Excluding((short)-2), null));

        await ExpectOrderAsync(
            "Wide",
            BindingSearch.Numbered(18_446_744_073_709_551_615ul, 1ul, 9_223_372_036_854_775_813ul, null, 1ul),
            (RangeEnd.Including(9_223_372_036_854_775_813ul), null),
            (null, RangeEnd.Excluding(18_446_744_073_709_551_615ul)));

        await ExpectOrderAsync(
            "Rights",
            BindingSearch.Numbered(7, 0, 4, 3, 5, 1, 6, 2, null),
            (RangeEnd.Excluding(1), RangeEnd.Including(5)));
    }

    [Fact]
    public async Task Integer_that_the_enum_type_does_not_allow_is_refused_as_a_value_and_as_a_bound()
    {
        await using var database = await BindingSchema.OpenAsync();
        await BindingSearch.InsertAsync(database, "Level", BindingSearch.Numbered((short)5));

        // Level declares neither zero nor -1 nor 301; Wide neither 2 nor the largest Int64; Rights no member
        // with the bit 8, which -1 has as well.
        (string Field, object Value)[] undeclared =
        [
            ("Level", (short)0), ("Level", (short)-1), ("Level", (short)301),
            ("Wide", 2ul), ("Wide", (ulong)long.MaxValue),
            ("Rights", 8), ("Rights", 9), ("Rights", -1),
        ];

        foreach (var (field, value) in undeclared)
        {
            var index = $"By{field}";
            var search = $"{index} with {value}";

            await BindingSearch.ExpectRefusedAsync(database, index, TypedValues.Equal(value), $"{search}, as the value");

            foreach (var inclusive in new[] { true, false })
            {
                await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(TypedValues.Bound(value, inclusive), null), $"{search}, as a lower bound");
                await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(null, TypedValues.Bound(value, inclusive)), $"{search}, as an upper bound");
            }
        }
    }

    [Fact]
    public async Task Refusal_of_an_integer_the_enum_type_does_not_allow_says_so_in_the_words_of_the_validation_of_a_write()
    {
        await using var database = await BindingSchema.OpenAsync();
        var byLevel = database.Set<Item>().Index("ByLevel");
        var byRights = database.Set("Item").Index("ByRights");

        // The integer given as an Int32 is named in the underlying type it was bound to.
        var level = Assert.Throws<ArgumentException>(() => byLevel.Find(IndexKey.Equal(0)).ToList());
        var rights = Assert.Throws<ArgumentException>(() => byRights.Find(IndexKey.All.Range(IndexBound.Inclusive(8L), null)).ToList());

        Assert.Equal("key", level.ParamName);
        Assert.Contains("the value 0, which the enum type 'Level' does not declare", level.Message, StringComparison.Ordinal);
        Assert.Contains("the value 8, which has a bit that no member of the Flags enum type 'Rights' declares", rights.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Combination_of_declared_bits_that_no_member_names_and_zero_as_IsValid_says_are_values_of_a_Flags_part()
    {
        await using var database = await BindingSchema.OpenAsync();
        var rightsType = BindingSchema.Utf8.EnumTypes.Single(enumType => enumType.Name == "Rights");
        var stored = BindingSearch.Numbered(0, 5, 7, 3);

        await BindingSearch.InsertAsync(database, "Rights", stored);

        // Read | Delete and Read | Write | Delete are combinations of declared bits that no member names.
        Assert.DoesNotContain(rightsType.Members, member => member.Value == 5 || member.Value == 7);
        Assert.True(rightsType.IsValid(0));

        foreach (var value in new[] { 0, 5, 7 })
        {
            await BindingSearch.ExpectAsync(database, "Rights", IndexKey.Equal(value), reverse: false, ValueOracle.Equal(stored, value), $"ByRights = {value}");
        }

        await BindingSearch.ExpectRangeAsync(database, "Rights", stored, RangeEnd.Including(5), RangeEnd.Excluding(7));
    }

    [Fact]
    public async Task Integer_of_another_width_that_fits_the_underlying_type_is_the_same_Enum_value()
    {
        await using var database = await BindingSchema.OpenAsync();
        var levels = BindingSearch.Numbered((short)-300, (short)5, (short)300);
        List<Stored> wides = [new(11, 1ul), new(12, 9_223_372_036_854_775_813ul)];

        await BindingSearch.InsertAsync(database, "Level", levels);
        await BindingSearch.InsertAsync(database, "Wide", wides);

        await BindingSearch.ExpectAsync(database, "Level", IndexKey.Equal(-300), reverse: false, [levels[0]], "ByLevel = -300 as an Int32");
        await BindingSearch.ExpectAsync(database, "Level", IndexKey.Equal(300L), reverse: false, [levels[2]], "ByLevel = 300 as an Int64");
        await BindingSearch.ExpectAsync(database, "Level", IndexKey.Equal((sbyte)5), reverse: false, [levels[1]], "ByLevel = 5 as an Int8");
        await BindingSearch.ExpectAsync(database, "Wide", IndexKey.Equal(1), reverse: false, [wides[0]], "ByWide = 1 as an Int32");
        await BindingSearch.ExpectAsync(database, "Level", IndexKey.All.Range(IndexBound.Inclusive(-2), IndexBound.Exclusive(300L)), reverse: false, [levels[1]], "ByLevel from -2 to 300 as an Int32 and an Int64");
    }

    [Fact]
    public async Task Value_of_a_dot_net_enum_is_refused_for_an_Enum_part_as_a_value_and_as_a_bound()
    {
        await using var database = await BindingSchema.OpenAsync();

        await BindingSearch.ExpectRefusedAsync(database, "ByLevel", IndexKey.Equal(DotNetLevelEnum.High), "ByLevel = DotNetLevelEnum.High");
        await BindingSearch.ExpectRefusedAsync(database, "ByLevel", IndexKey.All.Range(IndexBound.Inclusive(DotNetLevelEnum.Low), null), "ByLevel from DotNetLevelEnum.Low");
        await BindingSearch.ExpectRefusedAsync(database, "ByLevel", IndexKey.All.Range(null, IndexBound.Exclusive(DotNetLevelEnum.Highest)), "ByLevel to DotNetLevelEnum.Highest");
    }

    // The whole index of the field over the entities, which a database of their own holds, and each range,
    // in both directions.
    private static async Task ExpectOrderAsync(string field, List<Stored> stored, params (RangeEnd? From, RangeEnd? To)[] ranges)
    {
        await using var database = await BindingSchema.OpenAsync();
        await BindingSearch.InsertAsync(database, field, stored);

        await BindingSearch.ExpectRangeAsync(database, field, stored, null, null);

        foreach (var (from, to) in ranges)
        {
            await BindingSearch.ExpectRangeAsync(database, field, stored, from, to);
        }
    }
}
