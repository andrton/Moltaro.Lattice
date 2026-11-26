using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Limits;

// The memory budget of an instance, MemoryLimit, bounds the memory the engine holds for the
// database. Without it an instance takes half of the memory the runtime reports as available to the
// process, computed when it opens; a process with several instances, and every deployment in
// production, sets it explicitly.
//
// Many callers may write at once. A write that waits for its turn is not refused for the number of
// writes before it, and writes to one entity run in the order of their calls. A write that the
// budget cannot hold is refused with WriteRejected, which names the Memory limit, before it has any
// effect, and it never runs later. A write that needs a new entity lifetime when all root
// addresses are in use or permanently retired reports RootSlots instead.
//
// The budget below holds every write the sample makes, so every write completes and the catch is
// not reached; a write that the budget cannot hold would reach it.
internal static class MemoryLimitSample
{
    public static async Task RunAsync(LatticeSchema schema)
    {
        Console.WriteLine("== Memory budget ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            MemoryLimit = 256L * 1024 * 1024,
        });

        var users = database.Set<User>();

        // Ten thousand writes submitted at once, and awaited afterwards.
        var inserts = new List<Task>();
        for (var id = 1L; id <= 10_000; id++)
        {
            inserts.Add(users.InsertAsync(new User { Id = id, Name = $"User {id}", Enabled = true }).AsTask());
        }

        // Three replaces of one entity submitted at once: the last call made is the state that stays.
        var renames = new List<Task>();
        for (var round = 1; round <= 3; round++)
        {
            renames.Add(users.ReplaceAsync(new User { Id = 1, Name = $"User 1, rename {round}", Enabled = true }).AsTask());
        }

        try
        {
            await Task.WhenAll(inserts.Concat(renames));

            Console.WriteLine($"All {inserts.Count + renames.Count} writes completed within the budget");
            Console.WriteLine($"User 1 after its three renames: {users.Get(1L)?.Name}");
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.WriteRejected &&
                                                 exception.Limit is LatticeLimitEnum.Memory or LatticeLimitEnum.RootSlots)
        {
            var completed = inserts.Concat(renames).Count(write => write.IsCompletedSuccessfully);

            Console.WriteLine($"A write was refused at the {exception.Limit} limit and had no effect. {completed} writes completed.");
        }

        Console.WriteLine();
    }
}
