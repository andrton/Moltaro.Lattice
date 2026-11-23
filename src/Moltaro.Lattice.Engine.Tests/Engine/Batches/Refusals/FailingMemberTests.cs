namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Refusals;

// A write batch reports the member with the lowest number among those that fail (Q58 of the open questions),
// in whatever entity types and indexes they fail, whatever the order in which the entity types of the batch
// first appear: one member takes a Number of ByNumber and one a Login of ByLogin that published entities hold,
// and the batch, its members added in either order, names its first member and the index that member
// violates. Nothing of it is written. UniqueSchema.
public sealed class FailingMemberTests
{
    [Theory]
    [InlineData("numbered first")]
    [InlineData("account first")]
    public async Task Of_two_members_of_two_entity_types_that_take_published_values_the_first_is_named(string order)
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var accounts = database.Set<UniqueSchema.Account>();
        var numbered = database.Set<UniqueSchema.Numbered>();
        await accounts.InsertAsync(UniqueSchema.Of(1, "taken"));
        await numbered.InsertAsync(new UniqueSchema.Numbered { Id = 1, Number = 7 });
        var before = database.Position;

        var batch = database.CreateWriteBatch();

        if (order == "numbered first")
        {
            batch.Insert(new UniqueSchema.Numbered { Id = 2, Number = 7 });
            batch.Insert(UniqueSchema.Of(2, "taken"));
        }
        else
        {
            batch.Insert(UniqueSchema.Of(2, "taken"));
            batch.Insert(new UniqueSchema.Numbered { Id = 2, Number = 7 });
        }

        var refused = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask());

        Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
        Assert.Equal(0, refused.OperationIndex);
        Assert.Equal(order == "numbered first" ? "ByNumber" : "ByLogin", refused.IndexName);
        Assert.Equal(before, database.Position);
        Assert.Null(accounts.Get(2L));
        Assert.Null(numbered.Get(2L));
    }
}
