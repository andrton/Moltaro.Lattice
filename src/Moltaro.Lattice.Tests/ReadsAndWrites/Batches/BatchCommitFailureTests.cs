namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// How CommitAsync reports a failure, by the rule of every asynchronous member (AGENTS.md, "Shape of the
// public API"): what the caller can know from its own arguments is thrown at the call — a batch submitted a
// second time, or with no operation —, and everything the database decides comes through the task: the
// refusal of a member, the close of the database, a token cancelled before the batch is executed, a session
// of another history. A failing member refuses the
// whole batch and carries its number (the remarks of ILatticeWriteBatch, "Executing").
public sealed class BatchCommitFailureTests
{
    [Fact]
    public async Task Batch_submitted_a_second_time_is_refused_at_the_call()
    {
        await using var database = await TestData.OpenMasterAsync();

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        await batch.CommitAsync();

        Assert.Throws<InvalidOperationException>(() => { _ = batch.CommitAsync(); });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.CommitAsync(new LatticeSession()); });
    }

    [Fact]
    public async Task Batch_with_no_operation_is_refused_at_the_call()
    {
        await using var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();

        Assert.Throws<InvalidOperationException>(() => { _ = batch.CommitAsync(); });
        Assert.Throws<InvalidOperationException>(() => { _ = batch.CommitAsync(new LatticeSession()); });
    }

    [Fact]
    public async Task Refusal_of_a_member_comes_through_the_task()
    {
        await using var database = await TestData.OpenMasterAsync();
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada again" });
        var commit = batch.CommitAsync();

        var refused = await Assert.ThrowsAsync<LatticeException>(() => commit.AsTask());

        Assert.Equal(LatticeErrorEnum.DuplicateKey, refused.Error);
        Assert.Equal(0, refused.OperationIndex);
    }

    [Fact]
    public async Task Close_of_the_database_comes_through_the_task()
    {
        var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });

        await database.CloseAsync();
        var commit = batch.CommitAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => commit.AsTask());
    }

    [Fact]
    public async Task Token_cancelled_before_the_batch_is_executed_comes_through_the_task_and_nothing_is_written()
    {
        await using var database = await TestData.OpenMasterAsync();
        var before = database.Position;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        var commit = batch.CommitAsync(cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.AsTask());
        Assert.Null(database.Set<User>().Get(1L));
        Assert.Equal(before, database.Position);
    }

    [Fact]
    public async Task Session_of_another_history_comes_through_the_task_and_nothing_is_written()
    {
        await using var other = await TestData.OpenMasterAsync();
        var session = new LatticeSession();
        await other.Set<User>().InsertAsync(new User { Id = 9, Name = "Elsewhere" }, session);

        await using var database = await TestData.OpenMasterAsync();
        var batch = database.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        var commit = batch.CommitAsync(session);

        var refused = await Assert.ThrowsAsync<LatticeException>(() => commit.AsTask());

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, refused.Error);
        Assert.Null(database.Set<User>().Get(1L));
    }

    [Fact]
    public async Task Replace_of_an_absent_entity_refuses_the_whole_batch_with_its_number()
    {
        await using var database = await TestData.OpenMasterAsync();
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Replace(new User { Id = 1, Name = "Ada Lovelace" });
        batch.Insert(new Tag { Name = "admin", Color = "red" });
        batch.Replace(new User { Id = 404, Name = "Nobody" });

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => batch.CommitAsync());

        Assert.Equal(2, refused.OperationIndex);
        Assert.Equal("Ada", database.Set<User>().Get(1L)!.Name);
        Assert.Null(database.Set<Tag>().Get("admin"));
        Assert.Equal(before, database.Position);
    }
}
