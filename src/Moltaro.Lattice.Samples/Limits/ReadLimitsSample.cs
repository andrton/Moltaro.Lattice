using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Limits;

// The options of a database can limit how much temporary memory and how much time inside the
// engine one enumeration, or one search through an index, may use. Neither is set by default, and
// without a limit an enumeration or a search of any length runs to its end. One that would exceed a
// limit fails with ReadLimitExceeded and names the limit; what it returned before stays valid, but
// it is not complete.
//
// The first enumeration below stays within both limits, so it runs to its end. The second runs on a
// database whose memory limit is small: it would remember more keys than the limit allows, so the
// catch is reached. A search through an index counts the same way, and FindAsync counts the result
// it holds as well until it returns: the search of a whole department fails under the small limit,
// while a page of it fits.
//
// This shows the intended use. The search by the leading field of an index is not implemented yet:
// the search of the department throws NotSupportedException, which Program.cs reports, and the
// sample stops there.
internal static class ReadLimitsSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Read limits ==");

        await using (var roomy = await OpenAsync(schema, readMemoryLimit: 16 * 1024 * 1024))
        {
            await AddUsersAsync(roomy, 100);
            CountUsers(roomy);
        }

        await using var small = await OpenAsync(schema, readMemoryLimit: 4 * 1024);
        await AddUsersAsync(small, 2_000);
        CountUsers(small);

        var byDepartmentAndName = small.Set<User>().Index("ByDepartmentAndName");

        try
        {
            var all = await byDepartmentAndName.FindAsync(IndexKey.Equal("Research"), new LatticeFindOptions());
            Console.WriteLine($"Found {all.Items.Count} users of Research within the limits of the options");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.ReadLimitExceeded)
        {
            Console.WriteLine($"The search of Research was stopped by the {exception.Limit} limit; it returned nothing.");
        }

        var page = await byDepartmentAndName.FindAsync(IndexKey.Equal("Research"), new LatticeFindOptions { Limit = 10 });
        Console.WriteLine($"A page of {page.Items.Count} users of Research fits the same limit; more: {page.HasMore}");

        Console.WriteLine();
    }

    private static ValueTask<ILatticeDatabase> OpenAsync(LatticeSchema schema, long readMemoryLimit) =>
        LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = readMemoryLimit,
            ReadTimeLimit = TimeSpan.FromSeconds(1),
        });

    private static async Task AddUsersAsync(ILatticeDatabase database, int count)
    {
        var users = database.Set<User>();
        for (var id = 1L; id <= count; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"User {id}", Department = "Research", Enabled = true });
        }
    }

    private static void CountUsers(ILatticeDatabase database)
    {
        var counted = 0;
        try
        {
            foreach (var user in database.Set<User>().Enumerate())
            {
                counted++;
            }

            Console.WriteLine($"Counted {counted} users within the limits of the options");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.ReadLimitExceeded)
        {
            Console.WriteLine($"Stopped by the {exception.Limit} limit after {counted} users; the count is not complete.");
        }
    }
}
