using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// No payload is pinned for the length of a load (brief of Epic 1, sprint S11, Acceptance): the capture
// copies one root at a time inside the protection of reads and sends it after it has left the protection
// (decided after the study, point 3), so a capture that waits in a send, for as long as its child does
// not read, keeps nothing of the arena of the master from being freed. While a child is held in the
// middle of its load (HeldLoad), the master replaces and deletes large pieces, copied and not copied yet,
// and a drain then frees everything those writes retired while the child is still Loading.
public sealed class NothingPinnedTests
{
    [Fact]
    public async Task Capture_that_waits_in_a_send_keeps_nothing_that_writes_retired_from_being_freed()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await HeldLoad.InsertAsync(master);
        await using var load = await HeldLoad.StartAsync(master);
        var pieces = master.Set<PieceSchema.Piece>();

        for (var id = HeldLoad.Small + 1L; id <= HeldLoad.Small + 8; id++)
        {
            await pieces.ReplaceAsync(Pieces.Versioned(id, version: 2, dataBytes: 16));
        }

        foreach (var id in HeldLoad.NotCopied)
        {
            await pieces.DeleteAsync(id);
        }

        // The capture may be copying a root at the moment of a drain, inside the protection; it leaves the
        // protection before it waits in its send, and then a drain frees everything.
        await Eventually.HoldsAsync(
            () =>
            {
                master.DrainRetired();
                var counters = master.Counters;

                return counters.Arena.RetiredBytes == 0 && counters.RetiredIndexBytes == 0;
            },
            "Within the bound a drain did not free what the writes retired while a capture waited in a send.");

        Assert.Equal(LatticeDatabaseStateEnum.Loading, load.Child.State);

        await load.ReleaseAsync();
        Pieces.ExpectSameAt(master, load.Child);
    }
}
