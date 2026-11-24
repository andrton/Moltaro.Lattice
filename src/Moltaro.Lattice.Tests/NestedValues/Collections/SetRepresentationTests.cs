namespace Moltaro.Lattice.Tests.NestedValues.Collections;

// A Set retains the first representation of equal members independently of the source comparer
// (schema-and-types.md, "Collection input contract"). DateTimeOffset is stored as a UTC instant.
public sealed class SetRepresentationTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Target",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "String", "Comparison": "OrdinalIgnoreCase" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Representations", "ClrType": "{{typeof(SetRepresentations).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Names", "Type": "Set", "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase" } },
                { "Id": 3, "Name": "Amounts", "Type": "Set", "Element": { "Type": "Decimal" } },
                { "Id": 4, "Name": "Numbers", "Type": "Set", "Element": { "Type": "Float64" } },
                { "Id": 5, "Name": "Instants", "Type": "Set", "Element": { "Type": "DateTimeOffset" } },
                { "Id": 6, "Name": "References", "Type": "Set", "Element": { "Type": "Reference", "EntityType": "Target" } },
                { "Id": 7, "Name": "Optional", "Type": "Set", "Element": { "Type": "Int32", "Nullable": true } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static readonly double FirstNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001);

    private static readonly double AnotherNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0002);

    private static readonly DateTimeOffset FirstInstant = new(2020, 1, 2, 15, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Set_keeps_the_first_schema_equal_member_on_a_master_and_a_child(bool hashed)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(Schema);
        var typed = master.Set<SetRepresentations>();
        var named = master.Set("Representations");
        var names = WithFillers(new[] { "Main", "MAIN" }, hashed, value => $"name{value}");
        var amounts = WithFillers(new[] { 1.00m, 1.0m }, hashed, value => (decimal)value);
        var numbers = WithFillers(new[] { FirstNaN, AnotherNaN, -0.0d, +0.0d }, hashed, value => (double)value);
        var instants = WithFillers(new[] { FirstInstant, FirstInstant.ToUniversalTime() }, hashed, value => FirstInstant.AddDays(value));
        var references = WithFillers(new[] { "ſun", "SUN" }, hashed, value => $"target{value}");
        var optional = WithFillers(new int?[] { null, null, 1 }, hashed, value => value);

        await typed.InsertAsync(new SetRepresentations
        {
            Id = 1,
            Names = new HashSet<string>(names, StringComparer.Ordinal),
            Amounts = WithComparer(amounts, (left, right) => decimal.GetBits(left).SequenceEqual(decimal.GetBits(right))),
            Numbers = WithComparer(numbers, (left, right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right)),
            Instants = WithComparer(instants, (left, right) => left.EqualsExact(right)),
            References = new HashSet<string>(references, StringComparer.Ordinal),
            Optional = [.. optional],
        });
        await named.InsertAsync(new LatticeRecord(named.EntityType)
        {
            ["Id"] = 2L,
            ["Names"] = names,
            ["Amounts"] = amounts,
            ["Numbers"] = numbers,
            ["Instants"] = instants,
            ["References"] = references,
            ["Optional"] = optional,
        });
        await using var child = await TestDatabases.OpenChildAsync(master);

        foreach (var database in new[] { master, child })
        {
            foreach (var key in new[] { 1L, 2L })
            {
                var byClass = database.Set<SetRepresentations>().Get(key)!;
                var byName = database.Set("Representations").Get(key)!;
                AssertRepresentations(byClass.Names, byClass.Amounts, byClass.Numbers, byClass.Instants, byClass.References, byClass.Optional, hashed);
                AssertRepresentations(
                    byName.Get<HashSet<string>>("Names")!, byName.Get<HashSet<decimal>>("Amounts")!,
                    byName.Get<HashSet<double>>("Numbers")!, byName.Get<HashSet<DateTimeOffset>>("Instants")!,
                    byName.Get<HashSet<string>>("References")!, byName.Get<HashSet<int?>>("Optional")!, hashed);
            }
        }
    }

    private static T[] WithFillers<T>(T[] first, bool hashed, Func<int, T> value) =>
        hashed ? [.. first, .. Enumerable.Range(2, 8).Select(value)] : first;

    private static HashSet<T> WithComparer<T>(IEnumerable<T> values, Func<T, T, bool> equal)
        where T : struct =>
        new(values, EqualityComparer<T>.Create(equal, _ => 0));

    private static void AssertRepresentations(
        HashSet<string> names, HashSet<decimal> amounts, HashSet<double> numbers, HashSet<DateTimeOffset> instants,
        HashSet<string> references, HashSet<int?> optional, bool hashed)
    {
        var fillers = hashed ? 8 : 0;
        Assert.Equal(fillers + 1, names.Count);
        Assert.Equal(fillers + 1, amounts.Count);
        Assert.Equal(fillers + 2, numbers.Count);
        Assert.Equal(fillers + 1, instants.Count);
        Assert.Equal(fillers + 1, references.Count);
        Assert.Equal(fillers + 2, optional.Count);

        Assert.Equal("Main", Assert.Single(names, name => name is "Main" or "MAIN"));
        Assert.Equal(decimal.GetBits(1.00m), decimal.GetBits(Assert.Single(amounts, amount => amount == 1)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(FirstNaN), BitConverter.DoubleToInt64Bits(Assert.Single(numbers, double.IsNaN)));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(Assert.Single(numbers, number => number == 0)));
        Assert.True(Assert.Single(instants, instant => instant == FirstInstant).EqualsExact(FirstInstant.ToUniversalTime()));
        Assert.True(references.TryGetValue("SUN", out var reference));
        Assert.Equal("ſun", reference);
        Assert.Single(optional, value => value is null);
    }
}
