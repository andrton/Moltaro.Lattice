namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// A String key that is not well-formed text, a surrogate without its pair, in its two places (the
// planner's answer to Q41; the remarks of ILatticeSet<TEntity>, ILatticeWriteBatch and IndexKey). As
// the value of the key field of an entity that is written, it is a value of the entity and is refused
// by the validation, ValidationFailed with the field named. As a key given as an argument, to a read,
// to a delete of a set or of a batch, or as a value of a key to search an index with, it is an
// ArgumentException, as a key of another type is. Tag has a String key, Name; User has the index
// ByEmail on the String Email and ByAgeAndName on Age and Name. On the placeholder and on the
// engine.
public sealed class MalformedTextKeyTests
{
    private const string Bad = "urg\uD83Dent";

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Key_field_of_a_written_entity_that_is_not_well_formed_is_ValidationFailed_with_the_field_named(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var tags = database.Set<Tag>();
        var tagRecords = database.Set("Tag");
        LatticeRecord RecordOf() => new(tagRecords.EntityType) { ["Name"] = Bad, ["Color"] = "red" };

        await ExpectRefusedAsync(() => tags.InsertAsync(new Tag { Name = Bad, Color = "red" }));
        await ExpectRefusedAsync(() => tags.UpsertAsync(new Tag { Name = Bad, Color = "red" }));
        await ExpectRefusedAsync(() => tags.ReplaceAsync(new Tag { Name = Bad, Color = "red" }));
        await ExpectRefusedAsync(() => tagRecords.InsertAsync(RecordOf()));
        await ExpectRefusedAsync(() => tagRecords.UpsertAsync(RecordOf()));

        var batch = database.CreateWriteBatch();
        ExpectRefused(() => batch.Insert(new Tag { Name = Bad, Color = "red" }));
        ExpectRefused(() => batch.Upsert(RecordOf()));
        Assert.Equal(0, batch.Count);

        Assert.Empty(tags.Enumerate());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Key_argument_that_is_not_well_formed_is_ArgumentException(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var tags = database.Set<Tag>();
        var tagRecords = database.Set("Tag");
        await tags.InsertAsync(new Tag { Name = "urgent", Color = "red" });
        Assert.True(tags.TryGet("urgent", out _, out var revision));

        // Reads, through the class and by name.
        Assert.Throws<ArgumentException>(() => tags.Get(Bad));
        Assert.Throws<ArgumentException>(() => tags.TryGet(Bad, out _));
        Assert.Throws<ArgumentException>(() => tags.TryGet(Bad, out _, out _));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tags.GetAsync(Bad, new LatticeSession()));
        Assert.Throws<ArgumentException>(() => tagRecords.Get(Bad));
        Assert.Throws<ArgumentException>(() => tagRecords.TryGet(Bad, out _, out _));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tagRecords.GetAsync(Bad, new LatticeSession()));

        // Deletes of a set, without and with a revision and a session.
        await Assert.ThrowsAsync<ArgumentException>(async () => await tags.DeleteAsync(Bad));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tags.DeleteAsync(Bad, revision));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tags.DeleteAsync(Bad, new LatticeSession()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tagRecords.DeleteAsync(Bad));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tagRecords.DeleteAsync(Bad, revision, new LatticeSession()));

        // Deletes added to a batch.
        var batch = database.CreateWriteBatch();
        Assert.Throws<ArgumentException>(() => batch.Delete<Tag>(Bad));
        Assert.Throws<ArgumentException>(() => batch.Delete<Tag>(Bad, revision));
        Assert.Throws<ArgumentException>(() => batch.Delete("Tag", Bad));
        Assert.Throws<ArgumentException>(() => batch.Delete("Tag", Bad, revision));
        Assert.Equal(0, batch.Count);

        // The values of a key to search an index with, an equal value and a bound of a range.
        var byEmail = database.Set<User>().Index("ByEmail");
        Assert.Throws<ArgumentException>(() => byEmail.Find(IndexKey.Equal(Bad)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await byEmail.FindAsync(IndexKey.Equal(Bad), new LatticeFindOptions()));
        Assert.Throws<ArgumentException>(() => database.Set("User").Index("ByEmail").Find(IndexKey.Equal(Bad)));
        Assert.Throws<ArgumentException>(() => database.Set<User>().Index("ByAgeAndName").Find(IndexKey.Equal(36).Range(IndexBound.Inclusive(Bad), null)));

        // Nothing was deleted.
        Assert.Equal("red", tags.Get("urgent")!.Color);
    }

    private static async Task ExpectRefusedAsync(Func<ValueTask<LatticeWriteResult>> write)
    {
        var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, write);

        Assert.Contains("Name", exception.Message, StringComparison.Ordinal);
    }

    private static void ExpectRefused(Action add)
    {
        var exception = Assert.Throws<LatticeException>(add);

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        Assert.Contains("Name", exception.Message, StringComparison.Ordinal);
    }
}
