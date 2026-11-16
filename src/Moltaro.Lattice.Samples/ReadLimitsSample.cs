using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// An enumeration can be given limits: how much temporary memory and how much time inside the
// engine it may use. None is set by default, and without a limit an enumeration of any length
// runs to its end. A limit set in the options applies to every enumeration of the database; one
// enumeration can set another value, but cannot lift it. An enumeration that would exceed a
// limit fails with ReadLimitExceeded and names the limit; what it returned before stays valid,
// but it is not complete.
//
// This shows the intended use. Enumerating with limits is not implemented yet: the first
// Enumerate with limits below throws NotSupportedException, which Program.cs reports.
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

        // A report may take longer than the options allow; it keeps their memory limit.
        var counted = 0;
        try
        {
            foreach (var user in users.Enumerate(new LatticeReadLimits(Time: TimeSpan.FromSeconds(30))))
            {
                counted++;
            }

            Console.WriteLine($"Counted {counted} users");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.ReadLimitExceeded)
        {
            Console.WriteLine($"Stopped by the {exception.Limit} limit after {counted} users; the count is not complete.");
        }

        // Records take the same limits, here after the position of a session.
        var session = new LatticeSession();
        await foreach (var record in database.Set("User").EnumerateAsync(session, new LatticeReadLimits(Memory: 1024 * 1024)))
        {
            Console.WriteLine($"User as a record: {record["Name"]}");
        }

        Console.WriteLine();
    }
}
