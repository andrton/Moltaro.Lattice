namespace Moltaro.Lattice.Tests.Indexes;

// A search by the full key of a declared index returns exactly the entities whose current state has
// that key (decision on index access: equality over the full key of a single-field or composite index;
// docs/design/ordered-index.md, "The search by a full key"), through a class and by name alike, Find and
// FindAsync alike:
// - an index on one field and composite indexes of two and of four fields, the most values a key takes;
// - several entities with one key are all returned, also 3,000 of them, each once; entities of one key
//   come in no order the contract states, so the tests compare sets;
// - a null part is a value of the index like any other: it finds the entities without a value there, in
//   a field of its own and as a part of a composite key;
// - a key is a small value that serves any number of searches, from any thread, and a copy of it is the
//   same key;
// - what a search returns is the caller's own: changing the items of a result, through a cast if one is
//   possible, changes no other result and nothing stored.
// The members are written by name and by class; MemberSchema says what is indexed.
public sealed class ExactSearchTests
{
    [Fact]
    public async Task Index_on_one_field_returns_exactly_the_entities_with_the_value_through_a_class_and_by_name()
    {
        await using var database = await OpenWithMembersAsync(30);
        var byClass = database.Set<Member>().Index("ByGroup");
        var byName = database.Set("Member").Index("ByGroup");

        for (var group = 0; group < 5; group++)
        {
            long[] expected = [.. Enumerable.Range(1, 30).Where(id => id % 5 == group).Select(id => (long)id)];

            Assert.Equal(expected, MemberSchema.IdsOf(byClass.Find(IndexKey.Equal(group))));
            Assert.Equal(expected, MemberSchema.IdsOf(byName.Find(IndexKey.Equal(group))));
            Assert.Equal(expected, MemberSchema.IdsOf((await byClass.FindAsync(IndexKey.Equal(group), new LatticeFindOptions())).Items));
            Assert.Equal(expected, MemberSchema.IdsOf((await byName.FindAsync(IndexKey.Equal(group), new LatticeFindOptions())).Items));
        }

        Assert.Empty(byClass.Find(IndexKey.Equal(5)));
        Assert.Empty(byName.Find(IndexKey.Equal(-1)));
        Assert.Empty((await byClass.FindAsync(IndexKey.Equal(int.MaxValue), new LatticeFindOptions())).Items);
    }

    [Fact]
    public async Task Composite_index_returns_the_entities_whose_every_field_has_its_value_of_the_key()
    {
        await using var database = await OpenWithMembersAsync(30);
        var byGroupAndLogin = database.Set<Member>().Index("ByGroupAndLogin");
        var byAllFour = database.Set("Member").Index("ByAllFour");

        // Member 7: group 2, login "login 7"; every login is its own, so the first field alone is not
        // enough, and the second compares without case.
        Assert.Equal([7L], MemberSchema.IdsOf(byGroupAndLogin.Find(IndexKey.Equal(2, "login 7"))));
        Assert.Equal([7L], MemberSchema.IdsOf(byGroupAndLogin.Find(IndexKey.Equal(2, "LOGIN 7"))));
        Assert.Empty(byGroupAndLogin.Find(IndexKey.Equal(3, "login 7")));
        Assert.Empty(byGroupAndLogin.Find(IndexKey.Equal(2, "login 8")));

        // Four fields, with and without the two nullable ones: an even member has a code and a date.
        Assert.Equal([8L], MemberSchema.IdsOf(byAllFour.Find(IndexKey.Equal(3, "login 8", CodeOf(8), JoinedOf(8)))));
        Assert.Equal([7L], MemberSchema.IdsOf(byAllFour.Find(IndexKey.Equal<int, string, Guid?, DateOnly?>(2, "login 7", null, null))));
        Assert.Empty(byAllFour.Find(IndexKey.Equal<int, string, Guid?, DateOnly?>(3, "login 8", CodeOf(8), null)));
        Assert.Empty(byAllFour.Find(IndexKey.Equal<int, string, Guid?, DateOnly?>(3, "login 8", null, JoinedOf(8))));
    }

    [Fact]
    public async Task Every_entity_with_the_searched_key_is_returned_once_also_three_thousand_of_them()
    {
        await using var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();

        // 3,000 members of group 7, and members of the groups beside it, inserted in an order that is
        // neither ascending nor descending.
        for (var step = 0; step < 3_200; step++)
        {
            var id = ((step * 7_919L) % 3_200) + 1;

            await members.InsertAsync(new Member { Id = id, Group = id <= 3_000 ? 7 : (id % 2 == 0 ? 6 : 8), Login = $"login {id}" });
        }

        long[] seven = [.. Enumerable.Range(1, 3_000).Select(id => (long)id)];

        Assert.Equal(seven, MemberSchema.IdsOf(members.Index("ByGroup").Find(IndexKey.Equal(7))));
        Assert.Equal(seven, MemberSchema.IdsOf(database.Set("Member").Index("ByGroup").Find(IndexKey.Equal(7))));
        Assert.Equal(seven, MemberSchema.IdsOf((await members.Index("ByGroup").FindAsync(IndexKey.Equal(7), new LatticeFindOptions())).Items));
        Assert.Equal(100, members.Index("ByGroup").Find(IndexKey.Equal(6)).Count());
        Assert.Equal(100, members.Index("ByGroup").Find(IndexKey.Equal(8)).Count());
    }

    [Fact]
    public async Task Null_part_finds_the_entities_without_a_value_there_in_a_field_of_its_own_and_in_a_composite_key()
    {
        await using var database = await OpenWithMembersAsync(30);
        var byCode = database.Set<Member>().Index("ByCode");
        long[] odd = [.. Enumerable.Range(1, 30).Where(id => id % 2 == 1).Select(id => (long)id)];

        Assert.Equal(odd, MemberSchema.IdsOf(byCode.Find(IndexKey.Equal<Guid?>(null))));
        Assert.Equal(odd, MemberSchema.IdsOf(database.Set("Member").Index("ByCode").Find(IndexKey.Equal<Guid?>(null))));
        Assert.Equal([12L], MemberSchema.IdsOf(byCode.Find(IndexKey.Equal(CodeOf(12)))));

        // Null as the first part of a key of two, in the schema of TestData: Edsger has no age.
        await using var users = await TestData.OpenMasterAsync();
        await users.Set<User>().InsertAsync(new User { Id = 5, Name = "Edsger", Email = "shared@example.org" });
        await users.Set<User>().InsertAsync(new User { Id = 6, Name = "Edsger", Email = "edsger@example.org", Age = 41 });

        Assert.Equal(5, Assert.Single(users.Set<User>().Index("ByAgeAndName").Find(IndexKey.Equal<int?, string>(null, "Edsger"))).Id);
        Assert.Equal(6L, Assert.Single(users.Set("User").Index("ByAgeAndName").Find(IndexKey.Equal(41, "Edsger")))["Id"]);
    }

    [Fact]
    public async Task One_key_serves_any_number_of_searches_from_any_thread_and_a_copy_of_it_is_the_same_key()
    {
        await using var database = await OpenWithMembersAsync(30);
        var byGroupAndLogin = database.Set<Member>().Index("ByGroupAndLogin");
        var records = database.Set("Member").Index("ByGroupAndLogin");
        var key = IndexKey.Equal(2, "login 7");
        var copy = key;

        var searches = Enumerable.Range(0, 4)
            .Select(thread => OwnThread.Run(() =>
            {
                for (var search = 0; search < 1_000; search++)
                {
                    Assert.Equal(7, Assert.Single(byGroupAndLogin.Find(thread % 2 == 0 ? key : copy)).Id);
                    Assert.Equal(7L, Assert.Single(records.Find(key))["Id"]);
                }
            }))
            .ToArray();

        await Task.WhenAll(searches).WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(7, Assert.Single(byGroupAndLogin.Find(copy)).Id);
    }

    [Fact]
    public async Task Items_of_a_result_are_the_callers_own_and_changing_them_changes_no_other_result_and_nothing_stored()
    {
        await using var database = await OpenWithMembersAsync(30);
        var byGroup = database.Set<Member>().Index("ByGroup");
        var first = await byGroup.FindAsync(IndexKey.Equal(2), new LatticeFindOptions());
        var second = await byGroup.FindAsync(IndexKey.Equal(2), new LatticeFindOptions());

        Assert.NotSame(first.Items, second.Items);
        Assert.Equal(6, first.Items.Count);

        // A copy is changed in place, and the list is written through a cast where it allows one.
        first.Items[0].Login = "changed in a copy";

        if (first.Items is IList<Member> list)
        {
            try
            {
                list[1] = new Member { Id = 404 };
                list.Clear();
            }
            catch (NotSupportedException)
            {
                // A list that refuses to be written: the caller cannot change it.
            }
        }

        Assert.Equal(6, second.Items.Count);
        Assert.DoesNotContain(second.Items, member => member.Id == 404 || member.Login == "changed in a copy");
        Assert.All(byGroup.Find(IndexKey.Equal(2)), member => Assert.Equal($"login {member.Id}", member.Login));
    }

    // Members 1 to count: group id % 5, login "login {id}", a code and a date for an even id.
    private static async Task<ILatticeDatabase> OpenWithMembersAsync(int count)
    {
        var database = await MemberSchema.OpenAsync();
        var members = database.Set<Member>();
        var records = database.Set("Member");

        for (var id = 1L; id <= count; id++)
        {
            var member = new Member
            {
                Id = id,
                Group = (int)(id % 5),
                Login = $"login {id}",
                Code = id % 2 == 0 ? CodeOf(id) : null,
                Joined = id % 2 == 0 ? JoinedOf(id) : null,
            };

            // Half of them by name.
            await (id % 2 == 0 ? members.InsertAsync(member) : records.InsertAsync(MemberSchema.RecordOf(records, member)));
        }

        return database;
    }

    private static Guid CodeOf(long id) => new((int)id, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]);

    private static DateOnly JoinedOf(long id) => DateOnly.FromDayNumber(700_000 + (int)id);
}
