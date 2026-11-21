using Moltaro.Lattice.Engine;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Indexes.Search;

// A child builds its indexes itself, from what it loads, before it is ready, and keeps them while it
// follows its master (decision on physical index lifecycle, Q19: every node rebuilds its own indexes
// before readiness; docs/design/ordered-index.md, "A child"):
// - a search on a child right after its open has completed finds, for every key, what the master's
//   search finds, through a class and by name;
// - after the writes of the master — inserts, replaces that change a key, deletes — and a wait for the
//   position of the last of them, the child's search finds what the master's finds, and an old key
//   nothing;
// - a search with a session waits until the child has applied the position of the session before it
//   looks at its index: while the child holds back what it receives, the search does not complete, and
//   once the child applies the write, the search returns it.
// The tests hold back the application of a child through the switch of the engine
// (EngineDatabase.SuspendApplying), and wait through positions; nothing sleeps.
public sealed class SearchThroughAChildTests
{
    [Fact]
    public async Task Search_on_a_child_right_after_its_open_finds_what_the_master_finds()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, MemberSchema.Schema);
        var members = master.Set<Member>();

        for (var id = 1L; id <= 500; id++)
        {
            await members.InsertAsync(new Member { Id = id, Group = (int)(id % 7), Login = $"login {id % 50}", Code = id % 3 == 0 ? null : Guid.NewGuid() });
        }

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

        for (var group = 0; group < 7; group++)
        {
            ExpectSame(master, child, "ByGroup", IndexKey.Equal(group));
            ExpectSame(master, child, "ByGroupAndLogin", IndexKey.Equal(group, $"login {group}"));
        }

        ExpectSame(master, child, "ByCode", IndexKey.Equal<Guid?>(null));
        ExpectSame(master, child, "ByCode", IndexKey.Equal(members.Get(1L)!.Code));
    }

    [Fact]
    public async Task Search_on_a_child_finds_what_the_master_finds_after_its_writes_and_an_old_key_nothing()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, MemberSchema.Schema);
        var members = master.Set<Member>();
        var records = master.Set("Member");

        for (var id = 1L; id <= 100; id++)
        {
            await members.InsertAsync(new Member { Id = id, Group = (int)(id % 4), Login = $"login {id}" });
        }

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);

        for (var id = 101L; id <= 150; id++)
        {
            await records.InsertAsync(MemberSchema.RecordOf(records, new Member { Id = id, Group = 9, Login = $"login {id}" }));
        }

        for (var id = 1L; id <= 40; id++)
        {
            await members.ReplaceAsync(new Member { Id = id, Group = 8, Login = $"moved {id}" });
        }

        for (var id = 41L; id <= 60; id++)
        {
            await records.DeleteAsync(id);
        }

        await child.WaitForPositionAsync(master.Position);

        foreach (var group in new[] { 0, 1, 2, 3, 8, 9 })
        {
            ExpectSame(master, child, "ByGroup", IndexKey.Equal(group));
        }

        Assert.Empty(child.Set<Member>().Index("ByGroupAndLogin").Find(IndexKey.Equal(1, "login 1")));
        Assert.Empty(child.Set("Member").Index("ByGroupAndLogin").Find(IndexKey.Equal(1, "login 41")));
        Assert.Equal("moved 1", Assert.Single(child.Set<Member>().Index("ByGroupAndLogin").Find(IndexKey.Equal(8, "moved 1"))).Login);
        Assert.Equal(40, child.Set("Member").Index("ByGroup").Find(IndexKey.Equal(8)).Count());
    }

    [Fact]
    public async Task Search_with_a_session_on_a_child_does_not_complete_before_the_child_has_applied_the_position_of_the_session()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, MemberSchema.Schema);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var byClass = child.Set<Member>().Index("ByGroup");
        var byName = child.Set("Member").Index("ByGroup");
        var session = new LatticeSession();
        Task<LatticeFindResult<Member>> searchingByClass;
        Task<LatticeFindResult<LatticeRecord>> searchingByName;

        using (((EngineDatabase)child).SuspendApplying())
        {
            await master.Set<Member>().InsertAsync(new Member { Id = 1, Group = 4, Login = "ada" }, session);

            // What the child has now does not hold the write; a search that answered from it would
            // complete at once with nothing.
            Assert.Empty(byClass.Find(IndexKey.Equal(4)));

            searchingByClass = byClass.FindAsync(IndexKey.Equal(4), new LatticeFindOptions { Session = session }).AsTask();
            searchingByName = byName.FindAsync(IndexKey.Equal(4), new LatticeFindOptions { Session = session }).AsTask();

            Assert.False(searchingByClass.IsCompleted, "The search with the session completed while the child held back the write.");
            Assert.False(searchingByName.IsCompleted, "The search by name with the session completed while the child held back the write.");
        }

        var byClassResult = await searchingByClass.WaitAsync(Bound);
        var byNameResult = await searchingByName.WaitAsync(Bound);

        Assert.Equal(1, Assert.Single(byClassResult.Items).Id);
        Assert.Equal(1L, Assert.Single(byNameResult.Items)["Id"]);
        Assert.Equal(session.MinPosition, byClassResult.Position);
    }

    [Fact]
    public async Task Search_with_a_session_on_a_child_ends_its_wait_when_its_token_is_cancelled_and_refuses_a_session_of_another_history()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, MemberSchema.Schema);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        await using var other = await TestData.OpenMasterAsync(schema: MemberSchema.Schema);
        var byGroup = child.Set<Member>().Index("ByGroup");
        var session = new LatticeSession();

        using (((EngineDatabase)child).SuspendApplying())
        {
            await master.Set<Member>().InsertAsync(new Member { Id = 1, Group = 4, Login = "ada" }, session);

            using var cancellation = new CancellationTokenSource();
            var searching = byGroup.FindAsync(IndexKey.Equal(4), new LatticeFindOptions { Session = session }, cancellation.Token).AsTask();

            Assert.False(searching.IsCompleted, "The search with the session completed while the child held back the write.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searching.WaitAsync(Bound));
        }

        var foreign = new LatticeSession(other.Position);
        var mismatch = await Assert.ThrowsAsync<LatticeException>(async () => await byGroup.FindAsync(IndexKey.Equal(4), new LatticeFindOptions { Session = foreign }));

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, mismatch.Error);
    }

    // The child's search finds the entities the master's finds, by their ids, through a class and by name.
    private static void ExpectSame(ILatticeDatabase master, ILatticeDatabase child, string index, IndexKey key)
    {
        var expected = MemberSchema.IdsOf(master.Set<Member>().Index(index).Find(key));

        Assert.NotEmpty(expected);
        Assert.Equal(expected, MemberSchema.IdsOf(child.Set<Member>().Index(index).Find(key)));
        Assert.Equal(expected, MemberSchema.IdsOf(child.Set("Member").Index(index).Find(key)));
    }
}
