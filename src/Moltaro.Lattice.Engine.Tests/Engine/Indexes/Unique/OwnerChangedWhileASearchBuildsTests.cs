using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// A search by a full non-null key of a unique index returns at most one entity, also when the value changes
// owner while it runs, in every form that steps: FindAsync without a Limit and with one, whose look for a
// match after its Limit says that none follows, and the lazy search (docs/decisions/unique-indexes.md,
// "Non-null full-key equality has at most one matching root"; docs/design/ordered-index.md, "The search by a
// full key"). The owner is entity 1 in the layout of ValueAtTheEndOfALeaf, where the step that returns it
// cannot see the end of the run. A search builds each entity it returns after the step that found it and
// before the next step, and here the setter of Number moves the value meanwhile: entity 1 moves away and a
// new entity takes the value, so that FindAsync, which runs its steps in one call, meets the change between
// two of them.
public sealed class OwnerChangedWhileASearchBuildsTests
{
    private const long NewOwner = 1_000_000;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Hooked", "ClrType": "{{typeof(Hooked).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Number", "Type": "Int64" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByNumber", "Fields": [2], "Unique": true } ]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public async Task FindAsync_by_a_full_key_of_a_unique_index_returns_at_most_one_entity_when_the_value_changes_owner_while_it_runs(int? limit)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var set = database.Set<Hooked>();
        var value = await ArmAsync(database, set);

        var found = await set.Index("ByNumber").FindAsync(IndexKey.Equal(value), new LatticeFindOptions { Limit = limit });

        Assert.True(Hooked.Moved, "The premise: the value changed owner while the search ran.");
        Assert.True(found.Items.Count <= 1, $"FindAsync by the full key {value} returned {IdsOf(found.Items)}.");
    }

    [Fact]
    public async Task FindAsync_with_a_Limit_of_one_by_a_full_key_of_a_unique_index_says_that_nothing_follows_when_the_value_changes_owner_while_it_runs()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var set = database.Set<Hooked>();
        var value = await ArmAsync(database, set);

        var found = await set.Index("ByNumber").FindAsync(IndexKey.Equal(value), new LatticeFindOptions { Limit = 1 });

        Assert.True(Hooked.Moved, "The premise: the value changed owner while the search ran.");
        Assert.Equal(1L, Assert.Single(found.Items).Id);
        Assert.False(found.HasMore, $"FindAsync with a Limit of one by the full key {value} says that a match follows its owner.");
    }

    [Fact]
    public async Task Lazy_search_by_a_full_key_of_a_unique_index_returns_at_most_one_entity_when_the_value_changes_owner_while_it_builds_one()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var set = database.Set<Hooked>();
        var value = await ArmAsync(database, set);

        var found = set.Index("ByNumber").Find(IndexKey.Equal(value)).ToList();

        Assert.True(Hooked.Moved, "The premise: the value changed owner while the search ran.");
        Assert.True(found.Count <= 1, $"The lazy search by the full key {value} returned {IdsOf(found)}.");
    }

    // Makes the layout and arms the setter of Number: the build of the owner moves the value to a new entity.
    private static async Task<long> ArmAsync(EngineDatabase database, ILatticeSet<Hooked> set)
    {
        var value = await ValueAtTheEndOfALeaf.MakeAsync(database, "Hooked");

        Hooked.MoveWhenBuilt(value, () =>
        {
            set.ReplaceAsync(new Hooked { Id = 1, Number = 10 }).AsTask().WaitAsync(Bound).GetAwaiter().GetResult();
            set.InsertAsync(new Hooked { Id = NewOwner, Number = value }).AsTask().WaitAsync(Bound).GetAwaiter().GetResult();
        });

        return value;
    }

    private static string IdsOf(IEnumerable<Hooked> found) => string.Join(", ", found.Select(entity => entity.Id));

    // The entity of the tests: its setter of Number runs the move armed for the value it is given, once.
    public sealed class Hooked
    {
        // Static because the engine makes the entity through its constructor and its setters, which have no
        // other way to reach a test; only the tests of this class make a Hooked, and xUnit runs the tests of
        // one class one at a time.
        private static (long Value, Action Move)? _armed;

        private long _number;

        // Whether the move armed last has run.
        public static bool Moved { get; private set; }

        public long Id { get; set; }

        public long Number
        {
            get => _number;
            set
            {
                _number = value;

                if (_armed is { } armed && armed.Value == value)
                {
                    _armed = null;
                    armed.Move();
                    Moved = true;
                }
            }
        }

        // Arms the move for the next entity built with the value.
        public static void MoveWhenBuilt(long value, Action move)
        {
            _armed = (value, move);
            Moved = false;
        }
    }
}
