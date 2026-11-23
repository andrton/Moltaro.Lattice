namespace Moltaro.Lattice.Tests.NestedValues.Refusals;

// A read through a class of an entity type that holds an owned value or a List is not implemented yet and is
// refused with NotSupportedException before it reads (ILatticeSet<TEntity>): a synchronous read and the
// selection of an index throw it at the call; an asynchronous read throws at the call only what its
// arguments show, and reports the refusal through its task or its enumeration. The entity is written through
// the class and read by name.
public sealed class ClassReadRefusalTests
{
    [Fact]
    public async Task Synchronous_read_through_a_class_of_an_entity_type_with_owned_values_or_Lists_throws_NotSupportedException()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byClass = database.Set<Person>();

        await byClass.InsertAsync(NestedSchema.Full("ada"));

        Assert.Throws<NotSupportedException>(() => byClass.Get("ada"));
        Assert.Throws<NotSupportedException>(() => byClass.TryGet("ada", out _));
        Assert.Throws<NotSupportedException>(() => byClass.Enumerate().ToList());
        Assert.Throws<NotSupportedException>(() => byClass.Index("ByName"));
        Assert.NotNull(database.Set("Person").Get("ada"));
    }

    [Fact]
    public async Task Asynchronous_read_through_a_class_of_an_entity_type_with_owned_values_or_Lists_reports_NotSupportedException_through_its_task()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byClass = database.Set<Person>();
        var session = new LatticeSession();

        await byClass.InsertAsync(NestedSchema.Full("ada"), session);

        var read = byClass.GetAsync("ada", session);
        var enumeration = byClass.EnumerateAsync(session);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await read);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var person in enumeration)
            {
                Assert.Fail($"The enumeration returned {person.Code}.");
            }
        });
    }

    [Fact]
    public async Task Asynchronous_read_through_a_class_throws_at_the_call_what_its_arguments_show()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byClass = database.Set<Person>();

        Assert.Throws<ArgumentNullException>(() => byClass.GetAsync("ada", null!));
        Assert.Throws<ArgumentNullException>(() => byClass.EnumerateAsync(null!));
    }
}
