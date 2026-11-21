using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search.SearchAllocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// A value that is bound to its field by a conversion of the allowlist is not boxed: a search with it
// allocates what the same search with the value in the type of the field allocates (the brief of S15,
// part 2: a value that is converted is not boxed; IndexKey). Measured as the other searches are, with
// GC.GetAllocatedBytesForCurrentThread, after a first search of the same form, in the test collection of
// the tests that measure managed allocation: an Int32 for a Decimal part and a Float32 for a Float64 part,
// as the value of Equal and as both bounds of a range, through the class and by name, by Find and by
// FindAsync, each finding the one entity that holds the value.
[Collection<ManagedAllocationCollection>]
public sealed class ConvertedValueAllocationTests
{
    private static readonly LatticeFindOptions Options = new();

    private static readonly string[] Forms = ["Find through a class", "Find by name", "FindAsync through a class", "FindAsync by name"];

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Priced", "ClrType": "{{typeof(Priced).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Price", "Type": "Decimal" },
                { "Id": 3, "Name": "Weight", "Type": "Float64" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByPrice", "Fields": [2] }, { "Id": 2, "Name": "ByWeight", "Fields": [3] } ]
            }
          ]
        }
        """);

    [Fact(Skip = SkipReason.BeforeSprint15Part2)]
    public async Task Search_by_a_converted_value_allocates_what_the_search_by_the_value_in_the_type_of_the_field_allocates()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);

        await database.Set<Priced>().InsertAsync(new Priced { Id = 1, Price = 7m, Weight = 0.5 });
        await database.Set<Priced>().InsertAsync(new Priced { Id = 2, Price = 8m, Weight = 1.5 });

        (string Index, IndexKey Typed, IndexKey Converted)[] keys =
        [
            ("ByPrice", IndexKey.Equal(7m), IndexKey.Equal(7)),
            ("ByPrice", IndexKey.All.Range(IndexBound.Inclusive(7m), IndexBound.Exclusive(8m)), IndexKey.All.Range(IndexBound.Inclusive(7), IndexBound.Exclusive(8))),
            ("ByWeight", IndexKey.Equal(0.5), IndexKey.Equal(0.5f)),
            ("ByWeight", IndexKey.All.Range(IndexBound.Inclusive(0.5), IndexBound.Exclusive(1.5)), IndexKey.All.Range(IndexBound.Inclusive(0.5f), IndexBound.Exclusive(1.5f))),
        ];

        foreach (var (index, typed, converted) in keys)
        {
            foreach (var form in Forms)
            {
                var asTyped = Measure(() => Search(database, form, index, typed), found: 1);
                var asConverted = Measure(() => Search(database, form, index, converted), found: 1);

                Assert.True(asTyped == asConverted, $"{form} of {index} allocated {asConverted} bytes with a converted value and {asTyped} with the value in the type of the field.");
            }
        }
    }

    // One search in the form named, to its end; how many entities it returned.
    private static int Search(EngineDatabase database, string form, string index, IndexKey key) => form switch
    {
        "Find through a class" => Count(database.Set<Priced>().Index(index).Find(key)),
        "Find by name" => Count(database.Set("Priced").Index(index).Find(key)),
        "FindAsync through a class" => OwnThread.Wait(database.Set<Priced>().Index(index).FindAsync(key, Options)).Items.Count,
        _ => OwnThread.Wait(database.Set("Priced").Index(index).FindAsync(key, Options)).Items.Count,
    };

    private sealed class Priced
    {
        public long Id { get; set; }

        public decimal Price { get; set; }

        public double Weight { get; set; }
    }
}
