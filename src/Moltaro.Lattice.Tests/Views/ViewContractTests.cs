using Moltaro.Lattice.Tests.NestedValues;

namespace Moltaro.Lattice.Tests.Views;

public sealed class ViewContractTests
{
    [Fact]
    public void GetField_resolves_one_declared_name_by_ordinal_comparison()
    {
        Assert.True(NestedSchema.Schema.TryGetEntityType("Person", out var person));

        Assert.Same(person.Fields.Single(field => field.Name == "Home"), person.GetField("Home"));
        Assert.Throws<ArgumentException>(() => person.GetField("home"));
        Assert.Throws<ArgumentException>(() => person.GetField("Home.City"));
        Assert.Throws<ArgumentNullException>(() => person.GetField(null!));
    }

    [Fact]
    public async Task A_view_validates_the_key_session_and_callback_at_the_call()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var people = database.Set("Person");
        var session = new LatticeSession();

        Assert.Throws<ArgumentException>(() => people.TryView(7, static _ => 1, out _));
        Assert.Throws<ArgumentException>(() => people.TryView("\ud800", 1, static (_, state) => state, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, int>(null!, static _ => 1, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, int>("person", null!, out _));
        Assert.Throws<ArgumentNullException>(() => people.TryView<string, int, int>("person", 1, null!, out _));

        Assert.Throws<ArgumentException>(() => { _ = people.ViewAsync(7, session, static _ => 1); });
        Assert.Throws<ArgumentException>(() => { _ = people.ViewAsync("\ud800", session, 1, static (_, state) => state); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync("person", null!, static _ => 1); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync<string, int>("person", session, null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = people.ViewAsync<string, int, int>("person", session, 1, null!); });
    }
}
