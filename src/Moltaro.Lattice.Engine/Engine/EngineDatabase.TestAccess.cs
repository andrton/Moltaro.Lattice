using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine;

// What the tests of the engine and the measurement program reach of an instance beside the public
// contract (exception X3, docs/work/exceptions.md): the Create with their inputs, the counters, the
// contents of the text table and of the trees of the indexes, the nodes their pools hold, the live roots,
// the drain, the switches that
// hold a read, a growth, a refused write or the application of a child, the fault points, and the report of
// a child's catch-up. Each member says what it costs an instance whose tests do not use it.
internal sealed partial class EngineDatabase
{
    // Test-only observation of live owned backings, independent of the budget's byte totals.
    // The callback must not retain the backing objects or call the database.
    internal BudgetCapacitySample InspectBudget(Action<string, object>? observeBacking = null)
    {
        var (charged, peak, native) = _budget.Inspect(observeBacking);
        return new BudgetCapacitySample(charged, peak, native, _parts?.Delivery.CapacityBytes ?? 0);
    }

    internal void ResetBudgetPeak() => _budget.ResetPeak();

    // A test can hold a real charged allocation to align capacity return with a wire frame.
    internal IDisposable HoldBudgetBacking(int length) => new HeldBudgetBacking(_budget, length);

    private sealed class HeldBudgetBacking(MemoryBudget budget, int length) : IDisposable
    {
        private byte[]? _backing = budget.Allocate<byte>(length, nameof(HeldBudgetBacking));

        public void Dispose()
        {
            var backing = Interlocked.Exchange(ref _backing, null);
            if (backing is not null)
            {
                budget.Release(backing);
            }
        }
    }

    // For the tests of the engine and the measurement program (exception X3, docs/work/exceptions.md):
    // makes an instance as LatticeDatabase.Create does, with the same refusals, that takes what the inputs
    // give instead of what an instance makes for itself: the options of its arena, the allocator of its
    // native memory, the seed of the hashes of its primary indexes, the persistence step of its commit
    // stage, the source of the timestamps of its reads and the clock of its pending position waits, each
    // when it is set (EngineTestInputs). When the inputs give the options of the arena, MemoryLimit of the
    // options of the database is not read.
    public static EngineDatabase Create(LatticeDatabaseOptions options, EngineTestInputs inputs)
    {
        Check(options);
        ArgumentNullException.ThrowIfNull(inputs);

        return new EngineDatabase(options, inputs);
    }

    // What the engine holds at this moment, for the tests and later for diagnostics: taken between
    // two changes of the engine, under the lock of the writes, so that no change, no publication and no
    // freeing of what was retired is half done; the counters of the coordinator are written under the
    // same lock.
    public EngineCounters Counters => InTestWriteLock(static parts =>
    {
        var indexBytes = 0L;
        var replacementBytes = 0L;

        foreach (var operations in parts.Operations.Values)
        {
            indexBytes += operations.Table.IndexBytes;
            replacementBytes += operations.Table.ReplacementIndexBytes;
        }

        return new EngineCounters(
            parts.Arena.Counters,
            indexBytes,
            parts.Retired.IndexBytes,
            replacementBytes,
            parts.Roots.Count,
            (long)parts.Clock.Sequence,
            parts.Coordinator.QueuedWrites,
            parts.Coordinator.LargestCommitGroup,
            parts.Coordinator.LargestCommitGroupBytes,
            parts.Arena.MemoryLimit,
            parts.Delivery.ActiveCaptures,
            parts.Delivery.PendingBytes,
            parts.Arena.Texts.Count,
            parts.Arena.Texts.Bytes);
    });

    // For the tests of the engine (exception X3, docs/work/exceptions.md): every entry of the text table with
    // its exact stored bytes, in the encoding of the database, and its count of references, taken as Counters
    // is, under the lock of the writes between two changes of the engine, on a master and on a child. It does
    // not drain: what a root a write replaced or deleted holds until a drain frees it is counted. Unavailable
    // before the open and once Faulted, ObjectDisposedException after the close.
    public List<(byte[] Text, long References)> TextReferences() =>
        InTestWriteLock(static parts => parts.Arena.Texts.References());

    // For the tests of the layout invariants of the secondary indexes (exception X3, docs/work/exceptions.md):
    // every live node of the tree of the index of the entity type, in the order of a walk from the root, a
    // node before its children and the children of an inner node in their order, with its level, 0 for a
    // leaf, and its items: the entries of a leaf, each with the values of its parts as a record of the entity
    // type holds them and the primary key of the entity its identity names, and the separators of an inner
    // node, with no entity. Taken as
    // TextReferences is, under the lock of the writes between two changes of the engine, on a master and on
    // a child; it does not drain. ArgumentException for an index the entity type does not declare;
    // Unavailable before the open and once Faulted, ObjectDisposedException after the close.
    public List<(int Level, List<(object?[] Key, object? Entity)> Items)> IndexNodes(LatticeEntityType entityType, string indexName)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        LatticeIndex? definition = null;

        return InTestWriteLock(parts =>
        {
            var declaredIndex = definition!;
            var table = parts.Operations[entityType].Table;
            var nodes = new List<(int Level, List<(object?[] Key, object? Entity)> Items)>();

            var index = table.IndexOf(declaredIndex);
            var values = new object?[entityType.FieldsByOrdinal.Length];

            foreach (var (level, entries) in index.Tree.Nodes())
            {
                var items = new List<(object?[] Key, object? Entity)>(entries.Count);

                foreach (var entry in entries)
                {
                    var key = new object?[declaredIndex.Parts.Count];
                    object? entity = null;

                    index.Layout.ReadValues(parts.Arena, entry, key);

                    if (level == 0)
                    {
                        var root = parts.Roots.Entry(IndexLayout.IdentityOf(entry)).Root;

                        RootValues.Read(new RootReader(parts.Arena, table.Layout, root), entityType, values);
                        entity = values[entityType.Key.Ordinal];
                    }

                    items.Add((key, entity));
                }

                nodes.Add((level, items));
            }

            return nodes;
        }, () =>
        {
            if (!entityType.TryGetIndex(indexName, out definition))
            {
                throw new ArgumentException($"The entity type {entityType.Name} declares no index '{indexName}'.", nameof(indexName));
            }
        });
    }

    // For the audit of the ownership of blocks by the tests of the engine (exception X3,
    // docs/work/exceptions.md): the bytes of the nodes the secondary indexes of every entity type the engine
    // stores hold reserved for their publications and as their standing reserves, prepared blocks that no
    // tree names (Indexes.NodePool), each node a block of the node size of its index. Taken as TextReferences
    // is, under the lock of the writes between two changes of the engine, on a master and on a child; it does
    // not drain. Unavailable before the open and once Faulted, ObjectDisposedException after the close.
    public long ReservedNodeBytes() => InTestWriteLock(static parts =>
    {
        var bytes = 0L;

        foreach (var operations in parts.Operations.Values)
        {
            foreach (var index in operations.Table.Indexes)
            {
                bytes += (long)index.Tree.ReservedNodes * index.Layout.NodeBytes;
            }
        }

        return bytes;
    });

    // For the audit of the ownership of blocks by the tests of the engine (exception X3,
    // docs/work/exceptions.md): calls the action once for every live root of every entity type the engine
    // stores, with the arena, the layout of its entity type, the handle of its root entry and the address of
    // its root block, so that the test walks what the root owns itself (Storage.Roots.StructureWalk) and
    // compares it with the counters of the arena. Taken as TextReferences is, under the lock of the writes
    // between two changes of the engine, on a master and on a child, so nothing the action reads changes
    // while it reads; the action reads and changes nothing, and calls nothing of the database. It does not
    // drain: a root a write replaced or deleted is retired, not live, and is not given. Unavailable before
    // the open and once Faulted, ObjectDisposedException after the close.
    public void ForEachLiveRoot(Action<Arena, RootLayout, RootHandle, ArenaAddress> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        InTestWriteLock<object?>(parts =>
        {
            for (var slot = 0u; slot < (uint)parts.Roots.SlotExtent; slot++)
            {
                ref var entry = ref parts.Roots.EntryOfSlot(slot);

                if (entry.State == RootStateEnum.Live)
                {
                    action(parts.Arena, parts.Tables.Stored(entry.TypeId).Layout, new RootHandle(entry.Generation, slot), entry.Root);
                }
            }

            return null;
        });
    }

    // Frees every retired thing no read in progress can reach — an old root with its blocks, a replaced table
    // of the primary index, the entry of a deleted root — and returns. It does not wait and takes no timeout:
    // with no read in progress it leaves nothing retired, and what a read in progress can still reach stays
    // retired until a later drain. A test calls it before counting. Memory recovery and close share
    // the reclamation work. The test drain and memory recovery treat shrinking the empty queue as
    // best-effort. Close reports a trim failure (RetiredMemory.Drain, RetiredMemory.DrainAtClose).
    // Unavailable before the open and once Faulted, ObjectDisposedException after the close. It runs
    // under the lock of the writes, between two changes of the engine, because only that thread frees
    // (RetiredMemory).
    public void DrainRetired() => InTestWriteLock<object?>(static parts =>
    {
        parts.Retired.Drain();

        return null;
    });

    // Holds one audit snapshot across its synchronous test accessors. The same
    // thread reenters Writes when an accessor reads or drains the database.
    public void WithOwnershipAudit(Action audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        InTestWriteLock<object?>(_ =>
        {
            audit();
            return null;
        });
    }

    // A declaration check, when needed, follows the state check and precedes taking the barrier.
    private T InTestWriteLock<T>(Func<DatabaseParts, T> action, Action? checkDeclaration = null)
    {
        ThrowIfNotReady();
        checkDeclaration?.Invoke();
        _barrier.Enter();

        try
        {
            var parts = Part(_parts);

            lock (parts.Writes)
            {
                return action(parts);
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next read by key that
    // finds its root in the index, or the next root a step of a search through a secondary index takes,
    // runs the action, once, on its own thread and inside the protection of reads, after it has found the
    // root and before it reads it, with the handle of the root entry it found. A protected view with a
    // candidate miss offers the switch with a default handle before confirming the miss by sequence.
    // A step that makes room outside the protection takes the same root again in its next round.
    // A test waits inside the
    // action, with a bounded wait, to keep that read inside the protection while the test writes, drains,
    // counts and closes; when the action returns, the read goes on as it would have. Every other read
    // passes the point without running anything. The switch can be armed again while a read it held earlier
    // is still held, so that two reads are held at once. A read when no test has armed the switch pays
    // nothing for it beyond reading one field that is null (ReadEpochs.OfferReadSwitch). Unavailable before
    // the open, ObjectDisposedException after the close.
    public void ArmReadSwitch(Action<RootHandle> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfNotReady();

        _epochs.ArmReadSwitch(action);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next growth of the
    // primary index of the entity type runs the action, once, on the thread of the write that grows it and
    // inside the lock of the writes, after it has published the grown table and before it retires the
    // old one, while a step that entered before may still walk the old table and a step that enters
    // now walks the new one. A test waits inside the action, with a bounded wait, to keep the growth there
    // while it takes steps of an enumeration, or counts the growths; it neither writes nor counts the
    // counters meanwhile, since the lock of the writes is held. When the action returns the growth goes on
    // as it would have. A growth when no test has armed the switch pays one load of a field that is
    // null, and a read pays nothing. The entity type has to be one the engine stores. Unavailable
    // before the open, ObjectDisposedException after the close.
    public void ArmGrowthSwitch(LatticeEntityType entityType, Action action)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfNotReady();

        Part(_parts).Operations[entityType].Table.ArmGrowthSwitch(action);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next write that the arena
    // refused twice runs the action, once, at the first wait of its resolution, outside every lock, when it
    // knows it has to wait and before it waits, so that a test can tell that the write waits instead of
    // giving it a window of time. A refused write is resolved alone: while it holds back the preparation
    // of later writes it waits for the writes prepared and not published yet, then for the reads that
    // hold retired memory (WriteCoordinator, "The refusal"); the switch runs at the first of these waits
    // that the write has to make. On a child, which admits no write, the switch runs for the next
    // transaction its arena refused twice, once its applier knows it waits for the reads that hold
    // retired memory and before it waits (Child.Loading.ReceivedChanges.Apply). When the action returns the
    // write or the transaction waits as it would have. Only a write or a transaction the arena refused
    // loads the switch: no other write and no read pays anything for it (RetiredMemory.RunWaitSwitch).
    // Unavailable before the open, ObjectDisposedException after the close.
    public void ArmWaitSwitch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfNotReady();

        Part(_parts).Retired.ArmWaitSwitch(action);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next pass of a write through
    // the point throws InvalidOperationException with a message that names the point; tests only. Both points
    // lie after the persistence step, and a failure at either puts the instance into Faulted
    // (EngineFaultPointEnum): a write of the group published before the fault gets its result; the write
    // whose publication faulted and the writes of its group after it get IndeterminateOutcome, the first of
    // them with the injected exception as its InnerException; writes not yet dispatched get Unavailable;
    // every later operation, reads included, is Unavailable, and a close works. A point does nothing unless a
    // test arms it: a pass loads one field, under the lock of the writes it holds (WriteExecutor).
    // Unavailable before the open and once Faulted, ObjectDisposedException after the close.
    public void ArmFault(EngineFaultPointEnum point) => ArmFault(point, 0);

    // For the tests of the engine, as ArmFault(point): the given number of passes of writes go through the
    // point, and the pass after them throws, so that a test can fault the second write of a group and state
    // what the first, published before the fault, returns. ArmFault(point) is ArmFault(point, 0). An armed
    // point costs a pass what ArmFault(point) costs it, and a point nobody armed nothing more. A negative
    // number is ArgumentOutOfRangeException. No member of the contract lets a write of a group pass the point
    // before another faults there.
    public void ArmFault(EngineFaultPointEnum point, int timesToLetThrough)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timesToLetThrough);
        ThrowIfNotReady();

        Part(_parts).Coordinator.ArmFault(point, timesToLetThrough);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next publication that passes
    // the place given runs the action, once, on the publishing thread, on a master for a write or a batch
    // and on a child for a transaction it applies. The lock of the writes is held except at AfterFault
    // on the master. A test waits inside the action, with a bounded wait. An exception the action throws
    // fails the publication and faults the instance. At AfterFault the publication has already failed and
    // the action must return normally. A publication when no test has armed the switch pays one load of a
    // field that is null at each place, and a read pays nothing. Unavailable before the open and once
    // Faulted, ObjectDisposedException after the close.
    public void ArmPublicationSwitch(PublicationPointEnum point, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfNotReady();

        Part(_parts).Publisher.ArmPublicationSwitch(point, action);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the internal switch that holds
    // back a child's replication, so that a test can state what a child does while it has not applied what it
    // received. A Ready child holds back the application of every transaction it receives until the
    // suspension is disposed; holds of several suspensions add up. The transaction it has put together waits
    // where it was received, and the child receives nothing more meanwhile, so what its master sends waits in
    // the connection; its master goes on writing. Disposing it after the close does nothing; a suspension
    // held at the close does not keep the close from completing. Unavailable before the child is Ready,
    // ObjectDisposedException after the close, and InvalidOperationException on a master, which applies
    // nothing it receives.
    public IDisposable SuspendApplying()
    {
        ThrowIfNotReady();

        return _options.Role == LatticeRoleEnum.Child
            ? Part(_follower).Suspend()
            : throw new InvalidOperationException("A master applies nothing it receives; only a child holds back its application.");
    }

    // For the measurement program: what the catch-up of a child applied from LoadEnd to Ready; null on a
    // master and on a child that is not Ready yet. Set before the state becomes Ready and never changed
    // afterwards.
    internal CatchUpReport? CatchUp { get; private set; }
}
