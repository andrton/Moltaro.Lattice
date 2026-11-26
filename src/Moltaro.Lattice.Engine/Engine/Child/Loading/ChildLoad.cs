using System.Net.Sockets;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Child.Loading;

// The load of a child from its master: the steps of the open of a child that receive and place what the
// master sends, between the steps of that open that change the state of the database
// (EngineDatabase.LoadFromMasterAsync): the schema, checked against the handshake; the roots, placed into
// the empty database once its primary indexes are sized for them; and what the open reports for what ended
// it. Each runs on the task of the open; the sizing and every placement take the lock of the writes.
internal static class ChildLoad
{
    // The schema the master sends, with the counts of the roots of each entity type, once it is the schema
    // the handshake bound: the master announced its fingerprint in its HelloReply.
    public static async Task<(LatticeSchema Schema, long[] Counts)> ReceiveSchemaAsync(MasterLink master, Uri masterUri, long memoryLimit, CancellationToken cancellationToken)
    {
        var (schema, counts) = await master.ReceiveSchemaAsync(memoryLimit, cancellationToken).ConfigureAwait(false);

        if (schema.Fingerprint != master.Master.SchemaFingerprint)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master at {masterUri} sent a schema whose fingerprint {schema.Fingerprint} is not the {master.Master.SchemaFingerprint} its HelloReply announced.");
        }

        return (schema, counts);
    }

    // Sizes the primary indexes of the tables for the roots the master announced, then places every root
    // it sends (RootPlacement), and returns what LoadEnd announced.
    public static Task<LoadEndBody> ReceiveRootsAsync(MasterLink master, ReceivedChanges changes, EntityTables tables,
        RootTable roots, Lock writes, long[] counts, MemoryBudget budget, CancellationToken cancellationToken)
    {
        PresizeIndexes(tables.InSchemaOrder, roots, writes, counts, budget);

        return master.ReceiveRootsAsync(new RootPlacement(changes, tables), budget.Limit, cancellationToken);
    }

    // What the open of a child reports for what ended it: its own token, and a close that came while the open
    // waited, OperationCanceledException — the caller has no instance yet that could be unavailable, and
    // closing it withdraws the request to open; an address it cannot use, InvalidConfiguration; everything
    // that ends the open because of the master, Unavailable with the reason in the message. Closing says
    // whether the close of the instance has begun.
    public static Exception OpenFailure(Exception exception, CancellationToken cancellationToken, bool closing, Uri? masterUri, TimeSpan openTimeout) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => exception,
        OperationCanceledException or ObjectDisposedException when closing =>
            new OperationCanceledException("The instance was closed while it opened.", exception),
        LatticeException { Error: LatticeErrorEnum.Unavailable or LatticeErrorEnum.InvalidConfiguration } => exception,
        BudgetRefusedException => new LatticeException(LatticeErrorEnum.Unavailable,
            "This child cannot hold the state of its master within its available capacity.", exception),
        LatticeException { Error: LatticeErrorEnum.ValidationFailed or LatticeErrorEnum.InvalidSchema } invalid =>
            new LatticeException(LatticeErrorEnum.Unavailable, $"What the master at {masterUri} sent is not valid: {invalid.Message}", invalid),
        TimeoutException => new LatticeException(LatticeErrorEnum.Unavailable, $"Nothing arrived from the master at {masterUri} for the open timeout of {openTimeout}.", exception),
        IOException or SocketException => new LatticeException(LatticeErrorEnum.Unavailable, $"The connection to the master at {masterUri} ended before the load was complete.", exception),
        _ => exception,
    };

    // Sizes the primary index of each entity type for the roots its master announced, so that the load does
    // not grow it root by root. Each root needs at least a block for its fixed part. If the count
    // needs more than the memory limit, the open ends with Unavailable.
    private static void PresizeIndexes(IReadOnlyList<EntityTable> tables, RootTable roots, Lock writes,
        long[] counts, MemoryBudget budget)
    {
        var leastRoots = 0L;
        var announcedSlots = 0L;
        var indexCapacity = budget.Inspect(null).Charged;

        for (var index = 0; index < tables.Count; index++)
        {
            if (counts[index] == 0)
            {
                continue;
            }

            if (counts[index] > roots.AddressableSlots - announcedSlots)
            {
                throw new BudgetRefusedException(LatticeLimitEnum.RootSlots);
            }

            announcedSlots += counts[index];
            leastRoots += counts[index] * Arena.BlockSizeFor(tables[index].Layout.FixedSize - BlockHeader.Size);
            var replacement = tables[index].PresizeCapacity(counts[index]);
            if (replacement != 0)
            {
                if (replacement > budget.Limit - indexCapacity)
                {
                    throw new BudgetRefusedException(LatticeLimitEnum.Memory);
                }

                indexCapacity += replacement - PrimaryIndex.FirstCapacity;
            }
        }

        var pages = (int)((announcedSlots + RootTable.EntriesPerPage - 1) / RootTable.EntriesPerPage);
        leastRoots += pages * RootTable.PageBytes;
        if (pages != 0)
        {
            leastRoots += MemoryBudget.ArrayBytes<RootEntry[]>(pages);
        }

        if (leastRoots > budget.Limit - indexCapacity)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable,
                "The announced indexes and roots exceed the memory limit of this child.");
        }

        lock (writes)
        {
            for (var index = 0; index < tables.Count; index++)
            {
                if (counts[index] > 0)
                {
                    tables[index].Presize(counts[index]);
                }
            }
        }
    }
}
