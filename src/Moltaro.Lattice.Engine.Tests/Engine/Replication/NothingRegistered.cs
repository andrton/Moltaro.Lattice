using System.Diagnostics;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// Waits until a master holds nothing for a child any more: no capture in progress
// (EngineCounters.ActiveCaptures is 0), and no child to deliver to, so that a write it publishes retains
// nothing for delivery (EngineCounters.PendingDeliveryBytes is 0; brief of Epic 1, sprint S11, the
// planner's block of 15 November 2026 and his decision after the study, points 2 and 5). It is what "the
// child left nothing registered on the master" means from S11 on, when a master no longer refuses a write
// because of a child. The master learns that a child has gone only when its connection ends, a moment
// after the child closed, failed or vanished, so the test upserts the piece of the id given and reads the
// counters about once a millisecond until both are 0, and fails when they are not within the bound. Each
// attempt publishes its write; between two attempts the thread waits outside every lock of the engine.
internal static class NothingRegistered
{
    public static async Task ExpectAsync(EngineDatabase master, long id)
    {
        var pieces = master.Set<PieceSchema.Piece>();
        var waited = Stopwatch.StartNew();

        while (true)
        {
            await pieces.UpsertAsync(PieceSchema.Of(id, name: 8));

            var counters = master.Counters;

            if (counters.ActiveCaptures == 0 && counters.PendingDeliveryBytes == 0)
            {
                return;
            }

            if (waited.Elapsed > ReplicationNodes.Bound)
            {
                Assert.Fail($"Within the bound the master still holds {counters.ActiveCaptures} captures and {counters.PendingDeliveryBytes} bytes for delivery: a child that has gone is still registered on it.");
            }

            await Task.Delay(1);
        }
    }
}
