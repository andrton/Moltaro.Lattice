using Moltaro.Lattice.TestSupport.ApplicationCode;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// A walk whose entity the code of the application fails to build, a set accessor that throws, leaves the
// walk where it stood before that entity: the step that built it throws, and the next step returns it and
// the walk goes on, so it returns every entity of its search once, as an enumeration does
// (ApplicationCodeDuringReadsTests). By a full key, whose walk stands at an identity in the run of the key,
// and by a range, which stands after the entry of the entity it returned last. HookedEntity runs the code
// of the test when its Text is set; the index ByText is over Text.
public sealed class FailedBuildDuringAWalkTests
{
    private const int Rows = 5;

    // The entity whose build throws, counted from the first the walk builds.
    private const int Failing = 3;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Hooked", "ClrType": "{{typeof(HookedEntity).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Walk_whose_entity_fails_to_build_returns_it_at_its_next_step_and_every_entity_once(bool fullKey)
    {
        await using var database = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        });
        var hooked = database.Set<HookedEntity>();

        // By a full key, one text for every row; by a range, a text of its own for each, in the order of the ids.
        for (var id = 1L; id <= Rows; id++)
        {
            await hooked.InsertAsync(new HookedEntity { Id = id, Text = fullKey ? "t" : ((char)('a' + id - 1)).ToString() });
        }

        var key = fullKey ? IndexKey.Equal("t") : IndexKey.All.Range(IndexBound.Inclusive("a"), IndexBound.Inclusive("e"));
        using var walk = hooked.Index("ByText").Find(key).GetEnumerator();
        var built = 0;

        void FailAtTheEntity()
        {
            if (++built == Failing)
            {
                throw new InvalidOperationException("The set accessor of the test failed.");
            }

            HookedEntity.ArmSetText(FailAtTheEntity);
        }

        HookedEntity.ArmSetText(FailAtTheEntity);

        var returned = new List<long>();
        var failures = 0;

        for (var step = 0; step <= Rows; step++)
        {
            try
            {
                if (!walk.MoveNext())
                {
                    break;
                }

                returned.Add(walk.Current.Id);
            }
            catch (InvalidOperationException)
            {
                failures++;
            }
        }

        Assert.Equal(1, failures);
        Assert.False(walk.MoveNext());

        // The rows of one key come in no order the contract states; those of a range in the order of their texts.
        if (fullKey)
        {
            Assert.Equal(Enumerable.Range(1, Rows).Select(id => (long)id), returned.Order());
        }
        else
        {
            Assert.Equal(Enumerable.Range(1, Rows).Select(id => (long)id), returned);
        }
    }
}
