using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Batches;

// A child keeps no room of a large transaction once it has applied it: the list of the changes it prepares
// and the set of the keys a transaction names (Child.Loading.ReceivedChanges) are let go after a transaction
// above the size they are kept for, so that the managed memory of a child does not stay at the largest
// transaction it ever applied. A master commits a batch of 100,000 inserts; while the child holds its
// publication before the position (HeldPublication), the test watches every array of the two structures, the
// largest of which holds the 100,000; after the next transaction, of one effect, and a full collection, none
// of them is alive. The arrays are found through the applier of the child, which has no public surface for
// them. Every wait is bounded.
public sealed class LargeTransactionRoomTests
{
    private const int Effects = 100_000;

    [Fact(Skip = SkipReason.BatchThroughAChild)]
    public async Task Child_keeps_no_room_of_a_large_transaction_once_it_has_applied_the_next()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();

        using (var held = HeldPublication.Arm(child, PublicationPointEnum.BeforePosition))
        {
            await CommitLargeAsync(master, session);
            await held.ReachedAsync();

            var room = WatchRoom(child);

            held.Release();
            await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

            await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(Effects + 1, name: 1), session);
            await child.WaitForPositionAsync(session).AsTask().WaitAsync(Bound);

            Assert.Equal(0, Collect(room));
        }

        Assert.NotNull(child.Set<PieceSchema.Piece>().Get(Effects + 1L));
    }

    // The batch of the inserts, apart from the test, so that nothing of it stays on the stack of the test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CommitLargeAsync(EngineDatabase master, LatticeSession session)
    {
        var batch = master.CreateWriteBatch();

        for (var id = 1L; id <= Effects; id++)
        {
            batch.Insert(PieceSchema.Of(id, name: 1));
        }

        await batch.CommitAsync(session).AsTask().WaitAsync(Bound);
    }

    // A weak reference to every array that the list of the prepared changes and the set of the named keys of the
    // applier of the child hold now, the largest of which holds every effect of the transaction being published.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> WatchRoom(EngineDatabase child)
    {
        var changes = FieldOf(FieldOf(child, "_follower"), "_changes");
        var watched = new List<WeakReference>();
        var largest = 0;

        foreach (var structure in new[] { FieldOf(changes, "_prepared"), FieldOf(FieldOf(changes, "_keys"), "_keys") })
        {
            foreach (var field in structure.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.GetValue(structure) is Array array)
                {
                    watched.Add(new WeakReference(array));
                    largest = Math.Max(largest, array.Length);
                }
            }
        }

        Assert.True(largest >= Effects, $"The largest array of the room of the child holds {largest} items, fewer than the {Effects} effects of the transaction.");

        return watched;
    }

    private static object FieldOf(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    // The arrays still alive after a full collection.
    private static int Collect(List<WeakReference> room)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return room.Count(array => array.IsAlive);
    }
}
