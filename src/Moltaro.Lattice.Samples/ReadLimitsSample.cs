using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// The options of a database can limit how much temporary memory and how much time inside the
// engine one enumeration may use. Neither is set by default, and without a limit an enumeration
// of any length runs to its end. An enumeration that would exceed a limit fails with
// ReadLimitExceeded and names the limit; what it returned before stays valid, but it is not
// complete.
//
// This shows the intended use. Read limits are not enforced yet: the options are validated and
// otherwise not used, so the enumeration below runs to its end and the catch is not reached.
internal static class ReadLimitsSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Read limits ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = 16 * 1024 * 1024,
            ReadTimeLimit = TimeSpan.FromSeconds(1),
        });

        var users = database.Set<User>();
        for (var id = 1L; id <= 100; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"User {id}", Enabled = true });
        }

        var counted = 0;
        try
        {
            foreach (var user in users.Enumerate())
            {
                counted++;
            }

            Console.WriteLine($"Counted {counted} users within the limits of the options");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.ReadLimitExceeded)
        {
            Console.WriteLine($"Stopped by the {exception.Limit} limit after {counted} users; the count is not complete.");
        }

        Console.WriteLine();
    }
}
