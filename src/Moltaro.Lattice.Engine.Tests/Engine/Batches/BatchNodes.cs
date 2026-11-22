using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Batches.BatchSchema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches;

// The nodes of a test that reads while the batch of BatchSchema is published: a master that holds the
// state before the batch and, for the node "child", a child of it that has applied that state. The reader
// is the node whose publication the test holds: the master, which publishes the commit of the batch, or the
// child, which applies its transaction. The master commits the batch with Session, and NextSequence is the
// sequence of the position the batch takes, since nothing else is written.
internal sealed class BatchNodes : IAsyncDisposable
{
    private BatchNodes(EngineDatabase master, EngineDatabase? child, ulong nextSequence)
    {
        Master = master;
        Child = child;
        NextSequence = nextSequence;
    }

    public EngineDatabase Master { get; }

    public EngineDatabase? Child { get; }

    public EngineDatabase Reader => Child ?? Master;

    public LatticeSession Session { get; } = new();

    public ulong NextSequence { get; }

    public static async Task<BatchNodes> OpenAsync(string node)
    {
        var master = await ReplicationNodes.OpenMasterAsync(BatchSchema.Schema);

        try
        {
            await InsertBeforeAsync(master.Set<Item>());

            var child = node == "child" ? await ReplicationNodes.OpenChildAsync(master.ListenUri!) : null;

            await (child ?? master).WaitForPositionAsync(master.Position, Bound);

            return new BatchNodes(master, child, PositionSequence.Of(master.Position) + 1);
        }
        catch
        {
            await master.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Child is not null)
        {
            await Child.DisposeAsync();
        }

        await Master.DisposeAsync();
    }
}
