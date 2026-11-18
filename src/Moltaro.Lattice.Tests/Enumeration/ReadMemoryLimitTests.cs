using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// The memory limit of a read, LatticeDatabaseOptions.ReadMemoryLimit (brief of Epic 1, sprint S08,
// "What the two limits count", "After a failure" and "The limits of a read are options of the
// database only"):
// - It counts the temporary memory the engine holds for a pass: the keys it has returned, which it
//   remembers so that none is returned twice, with the text of a String key. That memory grows with
//   the pass. A pass that would exceed the limit fails from a step with LatticeException,
//   ReadLimitExceeded, and Limit ReadMemory. What it returned before stays valid: entities of one
//   written state, each once. It never ends as if it were complete: every further step fails with the
//   same error.
// - It does not count the copies the pass returns, so an entity larger than the limit is read.
// - A larger limit returns at least as many entities before it fails.
// The contract states no count of bytes, so the tests assert none. They choose sizes that any measure
// of the remembered keys tells apart: 2,000 Int64 keys take at least 16,000 bytes, which a limit of
// 4 KiB cannot hold, and 200 String keys of 2,000 characters at least 400,000 bytes, which a limit of
// 128 KiB cannot hold, while 200 keys of 7 characters fit in it. A pass under the smallest limit returns
// at least its first entity: three entities larger than the limit are read under it whole.
// Every form of an enumeration.
public sealed class ReadMemoryLimitTests
{
    private const int Users = 2_000;

    private const int Tags = 200;

    private const long Small = 4 * 1024;

    private const long Large = 8 * 1024 * 1024;

    [Theory]
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
    public async Task Enumeration_whose_remembered_keys_exceed_the_memory_limit_fails_with_ReadMemory_after_what_it_returned(string form)
    {
        await using var database = await OpenWithUsersAsync(Small);

        ExpectUsers(await ExpectReadMemoryAsync(UserPass.Of(database, form), Users));
    }

    [Fact]
    public async Task Larger_memory_limit_returns_at_least_as_many_entities_before_it_fails()
    {
        await using var small = await OpenWithUsersAsync(Small);
        await using var middle = await OpenWithUsersAsync(4 * Small);
        await using var larger = await OpenWithUsersAsync(16 * Small);
        await using var large = await OpenWithUsersAsync(Large);

        var underSmall = await ExpectReadMemoryAsync(UserPass.Of(small, EnumerationForms.ByClass), Users);
        var underMiddle = await ReturnedAsync(UserPass.Of(middle, EnumerationForms.ByClass));
        var underLarger = await ReturnedAsync(UserPass.Of(larger, EnumerationForms.ByClass));
        var underLarge = await ReturnedAsync(UserPass.Of(large, EnumerationForms.ByClass));

        Assert.True(underSmall.Count <= underMiddle.Count, $"{underSmall.Count} entities under {Small} bytes, {underMiddle.Count} under {4 * Small}.");
        Assert.True(underMiddle.Count <= underLarger.Count, $"{underMiddle.Count} entities under {4 * Small} bytes, {underLarger.Count} under {16 * Small}.");
        Assert.Equal(Users, underLarge.Count);
        ExpectUsers(underLarge);
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
    public async Task Entity_larger_than_the_memory_limit_is_read(string form)
    {
        await using var database = await OpenAsync(Small);
        var users = database.Set<User>();

        // A Name of 70,000 characters in chunks of the arena and an Email in a block of its own: far
        // more than the limit, in every copy the pass returns.
        for (var id = 1L; id <= 3; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = NameOf(id), Email = new string('e', 5_000) });
        }

        var returned = await UserPass.Of(database, form).ToListAsync();

        Assert.Equal([1L, 2L, 3L], returned.Select(item => item.Id).Order());
        Assert.All(returned, item => Assert.Equal(NameOf(item.Id), item.Name));

        static string NameOf(long id) => $"{id} ".PadRight(70_000, 'n');
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
    public async Task Text_of_the_String_keys_a_pass_remembers_counts_against_the_memory_limit(string form)
    {
        const long Limit = 128 * 1024;

        // Keys of 7 characters fit.
        await using var shortKeys = await OpenWithTagsAsync(Limit, tag => $"tag {tag:D3}");

        Assert.Equal(Tags, (await TagPass(shortKeys, form).ToListAsync()).Distinct().Count());

        // As many keys of 2,000 characters do not.
        await using var longKeys = await OpenWithTagsAsync(Limit, tag => $"{tag:D3}".PadRight(2_000, 't'));

        await ExpectReadMemoryAsync(TagPass(longKeys, form), Tags);
    }

    // A master of the engine whose options limit the memory of a read.
    private static ValueTask<ILatticeDatabase> OpenAsync(long readMemoryLimit) => TestDatabases.OpenAsync(new LatticeDatabaseOptions
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = TestData.Schema,
        ReadMemoryLimit = readMemoryLimit,
    });

    // The same, with the users 1 to Users, "user {id}".
    private static async Task<ILatticeDatabase> OpenWithUsersAsync(long readMemoryLimit)
    {
        var database = await OpenAsync(readMemoryLimit);
        var users = database.Set<User>();

        for (var id = 1L; id <= Users; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        return database;
    }

    // The same, with Tags tags, each with the name given for its number.
    private static async Task<ILatticeDatabase> OpenWithTagsAsync(long readMemoryLimit, Func<int, string> nameOf)
    {
        var database = await OpenAsync(readMemoryLimit);
        var tags = database.Set<Tag>();

        for (var tag = 0; tag < Tags; tag++)
        {
            await tags.InsertAsync(new Tag { Name = nameOf(tag), Color = "red" });
        }

        return database;
    }

    private static IAsyncEnumerable<string> TagPass(ILatticeDatabase database, string form) =>
        EnumerationForms.Of(form, database.Set<Tag>(), database.Set("Tag"), tag => tag.Name, record => record.Get<string>("Name")!);

    // The pass fails from a step with the memory limit named, after its first entity and before its
    // end, having returned no entity twice; every further step fails the same way. What it returned.
    private static async Task<List<T>> ExpectReadMemoryAsync<T>(IAsyncEnumerable<T> pass, int count)
    {
        await using var steps = pass.GetAsyncEnumerator();
        var (returned, failure) = await TakeAllAsync(steps);

        ExpectReadMemory(failure);
        Assert.InRange(returned.Count, 1, count - 1);
        Assert.Equal(returned.Count, returned.Distinct().Count());

        ExpectReadMemory(await Record.ExceptionAsync(async () => await steps.MoveNextAsync()));
        ExpectReadMemory(await Record.ExceptionAsync(async () => await steps.MoveNextAsync()));

        return returned;
    }

    // What a pass returned, at its end or before it failed with the memory limit named.
    private static async Task<List<T>> ReturnedAsync<T>(IAsyncEnumerable<T> pass)
    {
        await using var steps = pass.GetAsyncEnumerator();
        var (returned, failure) = await TakeAllAsync(steps);

        if (failure is not null)
        {
            ExpectReadMemory(failure);
        }

        return returned;
    }

    // Takes the steps of a pass to its end or to the first step that throws: what it returned, and
    // that exception, null at the end.
    private static async Task<(List<T> Returned, Exception? Failure)> TakeAllAsync<T>(IAsyncEnumerator<T> steps)
    {
        var returned = new List<T>();

        try
        {
            while (await steps.MoveNextAsync())
            {
                returned.Add(steps.Current);
            }
        }
        catch (Exception exception)
        {
            return (returned, exception);
        }

        return (returned, null);
    }

    private static void ExpectReadMemory(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadMemory, exception.Limit);
    }

    // Users of one written state each, the one every user was inserted with.
    private static void ExpectUsers(List<(long Id, string Name)> returned)
    {
        Assert.Equal(returned.Count, returned.Select(item => item.Id).Distinct().Count());
        Assert.All(returned, item => Assert.Equal($"user {item.Id}", item.Name));
        Assert.All(returned, item => Assert.InRange(item.Id, 1, Users));
    }
}
