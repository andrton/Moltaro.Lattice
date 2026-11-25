using Moltaro.Lattice.Tests.NestedValues;

namespace Moltaro.Lattice.Tests.Views;

public sealed class TypedViewCallsTests
{
    [Fact]
    public async Task A_typed_view_validates_the_key_session_and_viewer_at_the_call()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var people = database.Set<Person>();
        var session = new LatticeSession();

        Assert.Throws<ArgumentException>(() => people.TryView(7, static (PersonView _) => 1, out _));
        Assert.Throws<ArgumentException>(() => people.TryView("\ud800", 1, static (PersonView _, int number) => number, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, PersonView, int>(null!, static _ => 1, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, PersonView, int>("ada", null!, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, PersonView, int, int>("ada", 1, null!, out _));

        Assert.Throws<ArgumentException>(() => { _ = people.ViewAsync(7, session, static (PersonView _) => 1); });
        Assert.Throws<ArgumentException>(() => { _ = people.ViewAsync("\ud800", session, 1, static (PersonView _, int number) => number); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync("ada", null!, static (PersonView _) => 1); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync<string, PersonView, int>("ada", session, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync<string, PersonView, int, int>("ada", session, 1, null!); });
    }

    [Fact]
    public async Task A_typed_view_reports_the_same_hit_and_miss_positions_as_a_view_by_name()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var people = database.Set<Person>();
        var records = database.Set("Person");
        var written = await people.InsertAsync(NestedSchema.Full("ada"));

        Assert.True(people.TryView("ada", static (PersonView _) => (string?)null, out var hit));
        Assert.Null(hit);
        Assert.False(people.TryView("absent", static (PersonView _) => Unexpected(), out var miss));
        Assert.Null(miss);

        foreach (var key in new[] { "ada", "absent" })
        {
            var typedSession = new LatticeSession();
            var namedSession = new LatticeSession();
            var typed = await people.ViewAsync(key, typedSession, key, static (PersonView view, string name) =>
                view.Code.GetString() == name ? null : Unexpected());
            var named = await records.ViewAsync(key, namedSession, key, static (view, name) =>
                view["Code"].GetString() == name ? null : Unexpected());

            Assert.Equal(key == "ada", typed.Found);
            Assert.Equal(named.Found, typed.Found);
            Assert.Null(typed.Result);
            Assert.Equal(named.Result, typed.Result);
            Assert.Equal(written.Position, named.Position);
            Assert.Equal(named.Position, typed.Position);
            Assert.Equal(typed.Position, typedSession.MinPosition);
            Assert.Equal(named.Position, namedSession.MinPosition);
        }
    }

    [Fact]
    public async Task A_foreign_session_refuses_both_views_through_the_returned_task_even_when_the_key_exists()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await using var other = await NestedSchema.OpenMasterAsync();
        var people = database.Set<Person>();
        var records = database.Set("Person");
        await people.InsertAsync(NestedSchema.Full("ada"));
        Assert.NotNull(people.Get("ada"));
        Assert.NotNull(records.Get("ada"));
        var foreign = await other.Set<Person>().InsertAsync(NestedSchema.Full("other"));
        var typedSession = new LatticeSession(foreign.Position);
        var namedSession = new LatticeSession(foreign.Position);
        var typedCalls = 0;
        var namedCalls = 0;

        LatticeViewFunc<PersonView, int> typedViewer = _ => ++typedCalls;
        LatticeViewFunc<LatticeRecordView, int> namedViewer = _ => ++namedCalls;
        var typedLocal = await people.ViewAsync("ada", new LatticeSession(), typedViewer);
        var namedLocal = await records.ViewAsync("ada", new LatticeSession(), namedViewer);
        Assert.True(typedLocal.Found);
        Assert.True(namedLocal.Found);
        Assert.Equal(1, typedLocal.Result);
        Assert.Equal(1, namedLocal.Result);
        typedCalls = 0;
        namedCalls = 0;

        var typed = people.ViewAsync("ada", typedSession, typedViewer);
        var named = records.ViewAsync("ada", namedSession, namedViewer);

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, (await Assert.ThrowsAsync<LatticeException>(() => typed.AsTask())).Error);
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, (await Assert.ThrowsAsync<LatticeException>(() => named.AsTask())).Error);
        Assert.Equal(0, typedCalls);
        Assert.Equal(0, namedCalls);
        Assert.Equal(foreign.Position, typedSession.MinPosition);
        Assert.Equal(foreign.Position, namedSession.MinPosition);

        await database.DisposeAsync();

        var closed = people.ViewAsync("ada", new LatticeSession(), (PersonView _) => ++typedCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => closed.AsTask());
        Assert.Throws<ObjectDisposedException>(() => people.TryView("ada", (PersonView _) => ++typedCalls, out _));
        Assert.Equal(0, typedCalls);
    }

    [Fact]
    public void An_optional_borrowed_value_uses_a_presence_bit_for_the_default_value()
    {
        var absent = default(LatticeOptionalView<LatticeListView>);
        Assert.False(absent.HasValue);
        Assert.Throws<InvalidOperationException>(static () => { _ = default(LatticeOptionalView<LatticeListView>).Value; });

        var present = new LatticeOptionalView<LatticeListView>(default);
        Assert.True(present.HasValue);
        _ = present.Value;
    }

    private static string? Unexpected() => throw new InvalidOperationException("An absent entity ran its viewer.");
}
