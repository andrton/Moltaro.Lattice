using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Database;

// A close waits for the reads in progress before it frees the memory of the database (brief of
// Epic 1, sprint S06, "States"). A read that races a close returns the entity it found, whole, or,
// when it came after the close, fails with ObjectDisposedException; it never returns anything else.
// The test closes databases while readers on threads of their own read one entity in a loop, each
// until its read fails; nothing sleeps, and what it asserts does not depend on how the threads
// interleave. Twenty rounds of four readers.
public sealed class CloseDuringReadsTests
{
    private const int Rounds = 20;

    private const int Readers = 4;

    [Fact]
    public async Task Read_that_races_a_close_returns_the_whole_entity_or_ObjectDisposedException()
    {
        // A name in chunks and an email in a block of its own (brief of S05, "Where a value goes").
        var name = new string('n', 70_000);
        var email = new string('e', 1_000);

        for (var round = 0; round < Rounds; round++)
        {
            var database = await TestDatabases.OpenMasterAsync();
            var users = database.Set<User>();
            var userRecords = database.Set("User");
            await users.InsertAsync(new User { Id = 1, Name = name, Email = email, Age = round, Enabled = true });
            using var started = new CountdownEvent(Readers);
            var age = round;

            var readers = Enumerable.Range(0, Readers)
                .Select(reader => OwnThread.Run(() => ReadUntilClosed(started, reader % 2 == 0 ? () => ReadByClass(users, name, email, age) : () => ReadByName(userRecords, name, email, age))))
                .ToArray();

            // Every reader has read once before the close.
            Assert.True(started.Wait(Bound), "The readers did not start.");
            await database.CloseAsync();

            await Task.WhenAll(readers);
        }
    }

    // Reads until a read fails with ObjectDisposedException, and signals after its first read.
    private static void ReadUntilClosed(CountdownEvent started, Action read)
    {
        var signalled = false;

        while (true)
        {
            try
            {
                read();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (!signalled)
                {
                    signalled = true;
                    started.Signal();
                }
            }
        }
    }

    private static void ReadByClass(ILatticeSet<User> users, string name, string email, int age)
    {
        var user = users.Get(1L);

        Assert.NotNull(user);
        Assert.Equal(name, user.Name);
        Assert.Equal(email, user.Email);
        Assert.Equal(age, user.Age);
    }

    private static void ReadByName(ILatticeSet userRecords, string name, string email, int age)
    {
        var record = userRecords.Get(1L);

        Assert.NotNull(record);
        Assert.Equal(name, record["Name"]);
        Assert.Equal(email, record["Email"]);
        Assert.Equal(age, record["Age"]);
    }
}
