namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// What a child that is not ready serves: nothing. Every operation of the instance, on a child of a
// master of PieceSchema, refuses with Unavailable: the schema, the position, the listen address, the set
// through a class and by name, a write batch, and the waits for a position and for a session. It holds
// for a child that is Connecting or Loading (Opening.NotReadyTests) and for one that became Faulted
// (brief of Epic 1, sprint S11, decided after the study, point 9).
internal static class NotReady
{
    // A position of some history; a child that is not ready does not compare it with anything.
    private static readonly LatticePosition SomePosition = LatticePosition.Parse("0000000000000001-0");

    public static async Task ExpectEveryOperationUnavailableAsync(ILatticeDatabase child)
    {
        ExpectUnavailable(() => _ = child.Schema);
        ExpectUnavailable(() => _ = child.Position);
        ExpectUnavailable(() => _ = child.ListenUri);
        ExpectUnavailable(() => child.Set<PieceSchema.Piece>());
        ExpectUnavailable(() => child.Set("Piece"));
        ExpectUnavailable(() => child.CreateWriteBatch());

        await ExpectUnavailableAsync(async () => await child.WaitForPositionAsync(SomePosition, TimeSpan.FromSeconds(1)));
        await ExpectUnavailableAsync(async () => await child.WaitForPositionAsync(new LatticeSession()));
    }

    private static void ExpectUnavailable(Action operation)
    {
        var exception = Assert.Throws<LatticeException>(operation);

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
    }

    private static async Task ExpectUnavailableAsync(Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<LatticeException>(operation);

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
    }
}
