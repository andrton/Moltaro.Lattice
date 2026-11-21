using System.Globalization;

namespace Moltaro.Lattice.Tests.Indexes.Search;

// A write changes the entity and all its index entries in one publication, and a reader never finds an
// entity under a key it no longer has (docs/design/ordered-index.md, "An entry is a hint": a search
// returns an entity only if the current state of its root has the searched key, and an entity that has
// the key for the whole search is found). Readers search while one writer moves entities between two
// keys and back, deletes entities and inserts them again; through a class and by name, each search
// checks that
// - every entity it returns has the searched key, in a state the writer wrote: its fields agree with one
//   another as that state set them;
// - it returns no entity twice, unless the entity was deleted and created again while the search ran: a
//   recreated entity has a new root entry, so a new identity, which can lie after the one the search
//   returned (docs/design/ordered-index.md, "The search by a full key"; that no entity comes back twice
//   also then is chosen where ranges are walked). The writer stamps each recreation, once before the
//   delete and once after the insert, and a reader takes the stamps before and after its search: an
//   entity may come twice only if a recreation of it was under way or happened between the two;
// - every entity the writer never changes, whose key is the searched one for the whole search, is
//   returned.
// The readers run on threads of their own until the writer has made its writes; nothing sleeps.
public sealed class SearchUnderChurnTests
{
    private const int Stable = 64;

    private const int Moving = 64;

    private const int Recreated = 16;

    private const int Writes = 3_000;

    private static int Readers => Math.Clamp(Environment.ProcessorCount - 2, 2, 6);

    [Fact]
    public async Task Search_beside_a_writer_that_moves_keys_returns_only_entities_with_the_key_and_every_entity_that_kept_it()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var recreations = new long[Recreated];

        for (var id = 1L; id <= Stable; id++)
        {
            await members.InsertAsync(new Member { Id = id, Group = 1, Login = $"stable {id}" });
        }

        for (var id = 101L; id < 101 + Moving; id++)
        {
            await members.InsertAsync(MovingState(id, 0));
        }

        for (var id = 201L; id < 201 + Recreated; id++)
        {
            await members.InsertAsync(RecreatedState(id, 0));
        }

        await ReadersAndWriter.RunAsync(Readers, reader => ReaderOf(database, reader, recreations), () => OwnThread.Run(() => Write(members, recreations)));

        // After the writes, a search finds each entity under the key of its last state.
        Assert.Equal(Stable + Recreated + MovingIn(1), members.Index("ByGroup").Find(IndexKey.Equal(1)).Count());
        Assert.Equal(Moving - MovingIn(1), database.Set("Member").Index("ByGroup").Find(IndexKey.Equal(2)).Count());
    }

    // The searches of one reader: through a class or by name, in turn of the two keys.
    private static Action ReaderOf(ILatticeDatabase database, int reader, long[] recreations)
    {
        var byClass = database.Set<Member>().Index("ByGroup");
        var byName = database.Set("Member").Index("ByGroup");
        var byGroupAndLogin = database.Set<Member>().Index("ByGroupAndLogin");
        var search = 0L;

        return () =>
        {
            var group = (int)((reader + search++) % 2) + 1;
            var before = StampsOf(recreations);
            var found = reader % 2 == 0
                ? byClass.Find(IndexKey.Equal(group)).ToList()
                : byName.Find(IndexKey.Equal(group)).Select(MemberOf).ToList();
            var after = StampsOf(recreations);

            Assert.All(found, member => Assert.Equal(group, member.Group));
            Assert.All(found, ExpectWritten);

            foreach (var twice in found.GroupBy(member => member.Id).Where(same => same.Count() > 1).Select(same => same.Key))
            {
                var recreated = twice - 201;

                Assert.True(
                    recreated is >= 0 and < Recreated && (before[recreated] % 2 == 1 || before[recreated] != after[recreated]),
                    $"The search returned {twice} twice, and it was not deleted and created again while the search ran.");
            }

            if (group == 1)
            {
                var ids = found.Select(member => member.Id).ToHashSet();

                Assert.All(Enumerable.Range(1, Stable), id => Assert.Contains((long)id, ids));
            }

            // A stable member under a composite key, found exactly once at any moment.
            var stable = (search % Stable) + 1;

            Assert.Equal(stable, Assert.Single(byGroupAndLogin.Find(IndexKey.Equal(1, $"stable {stable}"))).Id);
        };
    }

    // The writer: moves a member to its next state at every write, and every tenth write deletes a
    // member of the third kind and inserts it again under a new login, its stamp counted up before the
    // delete and again after the insert, so that it is odd while the member is being recreated.
    private static void Write(ILatticeSet<Member> members, long[] recreations)
    {
        for (var write = 1; write <= Writes; write++)
        {
            var moving = 101 + ((write - 1) % Moving);

            OwnThread.Wait(members.ReplaceAsync(MovingState(moving, NumberOf(moving, write))));

            if (write % 10 == 0)
            {
                var recreated = 201 + ((write / 10) % Recreated);

                Interlocked.Increment(ref recreations[recreated - 201]);
                OwnThread.Wait(members.DeleteAsync((long)recreated));
                OwnThread.Wait(members.InsertAsync(RecreatedState(recreated, write)));
                Interlocked.Increment(ref recreations[recreated - 201]);
            }
        }
    }

    // The stamps of the recreations at this moment.
    private static long[] StampsOf(long[] recreations) => [.. Enumerable.Range(0, recreations.Length).Select(index => Volatile.Read(ref recreations[index]))];

    // The state number of a moving member after the given write: how many writes have moved it.
    private static long NumberOf(long id, int write) => ((write - 1) / Moving) + (((write - 1) % Moving) >= id - 101 ? 1 : 0);

    // How many moving members are in group 1 once the writer has made all its writes.
    private static int MovingIn(int group) =>
        Enumerable.Range(101, Moving).Count(id => MovingState(id, NumberOf(id, Writes)).Group == group);

    // A moving member in the state of the number: group 1 for an even number and 2 for an odd one, the
    // number in its note and in its login.
    private static Member MovingState(long id, long number) => new()
    {
        Id = id,
        Group = number % 2 == 0 ? 1 : 2,
        Login = string.Create(CultureInfo.InvariantCulture, $"moving {id} {number}"),
        Note = number.ToString(CultureInfo.InvariantCulture),
    };

    private static Member RecreatedState(long id, long number) => new()
    {
        Id = id,
        Group = 1,
        Login = string.Create(CultureInfo.InvariantCulture, $"again {id} {number}"),
        Note = number.ToString(CultureInfo.InvariantCulture),
    };

    // A member found is one state the writer wrote: its fields agree as that state set them.
    private static void ExpectWritten(Member member)
    {
        if (member.Id <= Stable)
        {
            Assert.Equal($"stable {member.Id}", member.Login);
            return;
        }

        var number = long.Parse(member.Note!, CultureInfo.InvariantCulture);
        var written = member.Id < 201 ? MovingState(member.Id, number) : RecreatedState(member.Id, number);

        Assert.Equal((written.Group, written.Login), (member.Group, member.Login));
    }

    private static Member MemberOf(LatticeRecord record) => new()
    {
        Id = record.Get<long>("Id"),
        Group = record.Get<int>("Group"),
        Login = record.Get<string>("Login")!,
        Note = record.Get<string>("Note"),
    };
}
