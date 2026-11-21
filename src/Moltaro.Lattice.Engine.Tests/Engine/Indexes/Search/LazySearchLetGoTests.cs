using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Sets;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// A lazy search, Find, lets go of what it remembers once it is over (docs/design/ordered-index.md, "How a
// range is walked": the keys it returned and the entries it stands at, its WalkProgress): at its end, when
// it is disposed, and when a read limit fails it, ReadTime as well as ReadMemory, since every later step
// fails the same way before it would read any of it. So an application that keeps the enumerable after
// that keeps only the object of the search. The test walks the 50 entities of a key of ByNumber of Counted
// (IndexSchema), takes a weak reference to what the search remembers once it has returned two of them, ends
// the search one of the four ways, and collects the garbage while it holds the enumerable: what the search
// remembered is collected. ReadTime is spent inside a step held at a root it takes, through the switch of the
// engine (EngineDatabase.ArmReadSwitch), for longer than the limit of ReadTimes; that step still returns its
// entity, and the next one fails.
public sealed class LazySearchLetGoTests
{
    private const int Rows = 50;

    // A memory limit that holds the keys of the first 12 entities and fails the search at the 13th, whose
    // room doubles the places of the keys (ReturnedKeys).
    private const long MemoryLimit = 200;

    public static TheoryData<string> Ends => ["walked to its end", "disposed before its end", "failed with ReadTime", "failed with ReadMemory"];

    [Theory]
    [MemberData(nameof(Ends))]
    public async Task Lazy_search_lets_go_of_what_it_remembered_once_it_is_over(string end)
    {
        await using var database = await OpenWithRowsAsync(
            end == "failed with ReadTime" ? ReadTimes.Limit : null,
            end == "failed with ReadMemory" ? MemoryLimit : null);
        var matches = database.Set<IndexSchema.Counted>().Index("ByNumber").Find(IndexKey.Equal(7L));
        using var walk = matches.GetEnumerator();

        Assert.True(walk.MoveNext());
        Assert.True(walk.MoveNext());

        var remembered = RememberedBy(walk);

        switch (end)
        {
            case "walked to its end":
                var returned = 2;

                while (walk.MoveNext())
                {
                    returned++;
                }

                Assert.Equal(Rows, returned);
                break;
            case "disposed before its end":
                walk.Dispose();
                break;
            case "failed with ReadTime":
                database.ArmReadSwitch(_ => Thread.Sleep(ReadTimes.BeyondTheLimit));
                Assert.True(walk.MoveNext());
                ExpectLimit(LatticeLimitEnum.ReadTime, Record.Exception(() => walk.MoveNext()));
                break;
            default:
                ExpectLimit(LatticeLimitEnum.ReadMemory, Record.Exception(() =>
                {
                    while (walk.MoveNext())
                    {
                    }
                }));
                break;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(remembered.IsAlive, $"A lazy search {end} still holds what it remembered.");
        GC.KeepAlive(matches);
    }

    // In a frame of its own, so that nothing of the test's frame references it: what the search remembers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RememberedBy(IEnumerator<IndexSchema.Counted> walk)
    {
        var search = Assert.IsType<IndexMatches<ClassCopy<IndexSchema.Counted>, IndexSchema.Counted>>(walk);

        return new WeakReference(Assert.IsType<WalkProgress>(search.Progress));
    }

    // A master of the engine with the entities 1 to 50 of Counted, of Number 7, under the read limits given.
    private static async Task<EngineDatabase> OpenWithRowsAsync(TimeSpan? readTimeLimit, long? readMemoryLimit)
    {
        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = IndexSchema.Utf8,
            ReadTimeLimit = readTimeLimit,
            ReadMemoryLimit = readMemoryLimit,
        });

        await database.OpenAsync();

        for (var id = 1L; id <= Rows; id++)
        {
            await database.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        return database;
    }

    private static void ExpectLimit(LatticeLimitEnum limit, Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(limit, exception.Limit);
    }
}
