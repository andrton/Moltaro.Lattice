using Moltaro.Lattice.Tests.NestedValues;

namespace Moltaro.Lattice.Tests.Views;

public sealed class ViewResultTests
{
    [Fact]
    public async Task A_null_callback_result_is_a_hit_and_an_absent_key_skips_the_callback()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var written = await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");
        var calls = 0;
        LatticeViewFunc<LatticeRecordView, string?> viewer = _ =>
        {
            calls++;
            return null;
        };

        Assert.True(people.TryView("ada", viewer, out var hit));
        Assert.Null(hit);
        Assert.False(people.TryView("absent", viewer, out var miss));
        Assert.Null(miss);
        Assert.Equal(1, calls);

        var hitSession = new LatticeSession();
        var hitAnswer = await people.ViewAsync("ada", hitSession, viewer);
        Assert.True(hitAnswer.Found);
        Assert.Null(hitAnswer.Result);
        Assert.Equal(written.Position, hitAnswer.Position);
        Assert.Equal(hitAnswer.Position, hitSession.MinPosition);

        var missSession = new LatticeSession();
        var missAnswer = await people.ViewAsync("absent", missSession, viewer);
        Assert.False(missAnswer.Found);
        Assert.Null(missAnswer.Result);
        Assert.Equal(written.Position, missAnswer.Position);
        Assert.Equal(missAnswer.Position, missSession.MinPosition);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_async_view_reports_a_closed_or_foreign_history_through_its_task()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await using var other = await NestedSchema.OpenMasterAsync();
        var foreign = await other.Set<Person>().InsertAsync(NestedSchema.Full("other"));
        var people = database.Set("Person");
        var calls = 0;
        LatticeViewFunc<LatticeRecordView, int> viewer = _ => ++calls;

        var mismatched = people.ViewAsync("ada", new LatticeSession(foreign.Position), viewer);
        var error = await Assert.ThrowsAsync<LatticeException>(() => mismatched.AsTask());
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, error.Error);
        await database.DisposeAsync();

        var closed = people.ViewAsync("ada", new LatticeSession(), viewer);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => closed.AsTask());
        Assert.Throws<ObjectDisposedException>(() => people.TryView("ada", viewer, out _));
        Assert.Equal(0, calls);
    }
}
