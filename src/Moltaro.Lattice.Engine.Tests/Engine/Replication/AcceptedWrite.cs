using System.Diagnostics;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// Waits until a master publishes a write again. Until S11 a master of the engine refuses every write with
// NotSupportedException while a child is connected or loading (brief of Epic 1, sprint S10, "Out of
// scope"), and the master learns that a child has gone only when its connection ends, a moment after the
// child closed, failed or vanished. A test tries an insert of a piece about once a millisecond until one
// is published, and fails when none is within the bound; a refused attempt has no effect. Between two
// attempts the thread waits outside every lock of the engine.
internal static class AcceptedWrite
{
    public static async Task UntilPublishedAsync(EngineDatabase master, long id)
    {
        var pieces = master.Set<PieceSchema.Piece>();
        var waited = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                await pieces.InsertAsync(PieceSchema.Of(id, name: 8));
                return;
            }
            catch (NotSupportedException)
            {
                if (waited.Elapsed > ReplicationNodes.Bound)
                {
                    Assert.Fail("The master refused every write within the bound: a child that has gone is still registered on it.");
                }
            }

            await Task.Delay(1);
        }
    }
}
