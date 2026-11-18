namespace Moltaro.Lattice.Tests.Database;

// After the close of a master in order, no write through a child that reads ends in
// IndeterminateOutcome (decided at the review of S12, point 3): each has its real outcome, here the
// success, or is Unavailable, "not executed". A write that succeeded is on the child, which applied it
// before it returned, and one that is Unavailable is not, since the child receives everything the master
// executed before the end of the connection. Round after round a child starts 8 inserts without awaiting
// one, and the master is closed at once, after a yield, or once the first insert has completed; before
// the fix the probe found 1,468 writes of 3,200 in IndeterminateOutcome.
public sealed class WritesAtTheCloseOfAMasterTests
{
    private const int Rounds = 10;

    private const int Writes = 8;

    public static IEnumerable<TheoryDataRow<string>> Moments =>
    [
        new("at once"),
        new("after a yield"),
        new("after the first completion"),
    ];

    [Theory]
    [MemberData(nameof(Moments))]
    public async Task Write_through_a_child_that_reads_has_its_real_outcome_or_is_Unavailable_when_its_master_closes(string moment)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
            await using var child = await TestData.OpenChildAsync(master.ListenUri!);
            var users = child.Users();
            var writes = Enumerable.Range(1, Writes).Select(id => users.InsertAsync(new User { Id = id, Name = $"user {id}" }).AsTask()).ToList();

            switch (moment)
            {
                case "after a yield":
                    await Task.Yield();
                    break;

                case "after the first completion":
                    await Task.WhenAny(writes);
                    break;
            }

            await master.CloseAsync();

            for (var id = 1; id <= Writes; id++)
            {
                var failure = await Record.ExceptionAsync(() => writes[id - 1]);

                if (failure is null)
                {
                    Assert.NotNull(users.Get(id));
                    continue;
                }

                Assert.Equal(LatticeErrorEnum.Unavailable, Assert.IsType<LatticeException>(failure).Error);
                Assert.Null(users.Get(id));
            }
        }
    }
}
