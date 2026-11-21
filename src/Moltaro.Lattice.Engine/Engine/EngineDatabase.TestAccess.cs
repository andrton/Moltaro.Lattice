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
// contents of the text table and of the trees of the indexes, the drain, the switches that hold a read, a
// growth, a refused write or the application of a child, the fault points, and the report of a child's
// catch-up. Each member says what it costs an instance whose tests do not use it.
internal sealed partial class EngineDatabase
{
    // For the tests of the engine and the measurement program (exception X3, docs/work/exceptions.md):
    // makes an instance as LatticeDatabase.Create does, with the same refusals, that takes what the inputs
    // give instead of what an instance makes for itself: the options of its arena, the allocator of its
    // native memory, the seed of the hashes of its primary indexes and the storage step of its commit
    // stage, each when it is set (EngineTestInputs). When the inputs give the options of the arena,
    // MemoryLimit of the options of the database is not read. Nothing else differs, and an instance made
    // by LatticeDatabase.Create pays nothing for it.
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
    public EngineCounters Counters
    {
        get
        {
            ThrowIfNotReady();
            _barrier.Enter();

            try
            {
                var parts = Part(_parts);

                lock (parts.Writes)
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
                }
            }
            finally
            {
                _barrier.Exit();
            }
        }
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): every entry of the text table with
    // its exact stored bytes, in the encoding of the database, and its count of references, taken as Counters
    // is, under the lock of the writes between two changes of the engine, on a master and on a child. It does
    // not drain: what a root a write replaced or deleted holds until a drain frees it is counted. Unavailable
    // before the open and once Faulted, ObjectDisposedException after the close.
    public List<(byte[] Text, long References)> TextReferences()
    {
        ThrowIfNotReady();
        _barrier.Enter();

        try
        {
            var parts = Part(_parts);

            lock (parts.Writes)
            {
                return parts.Arena.Texts.References();
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // For the tests of the layout invariants of the secondary indexes (exception X3, docs/work/exceptions.md):
    // every live node of the tree of the index of the entity type, in the order of a walk from the root, a
    // node before its children and the children of an inner node in their order, with its level, 0 for a
    // leaf, and its items: the entries of a leaf, each with the values of its parts as a record of the entity
    // type holds them and the primary key of the entity its identity names, and the separators of an inner
    // node, with no entity. An index of an entity type the engine does not store has no node. Taken as
    // TextReferences is, under the lock of the writes between two changes of the engine, on a master and on
    // a child; it does not drain. ArgumentException for an index the entity type does not declare;
    // Unavailable before the open and once Faulted, ObjectDisposedException after the close.
    public List<(int Level, List<(object?[] Key, object? Entity)> Items)> IndexNodes(LatticeEntityType entityType, string indexName)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ThrowIfNotReady();

        if (!entityType.TryGetIndex(indexName, out var definition))
        {
            throw new ArgumentException($"The entity type {entityType.Name} declares no index '{indexName}'.", nameof(indexName));
        }

        _barrier.Enter();

        try
        {
            var parts = Part(_parts);

            lock (parts.Writes)
            {
                var table = parts.Operations[entityType].Table;
                var nodes = new List<(int Level, List<(object?[] Key, object? Entity)> Items)>();

                if (table.IndexOf(definition) is not { } index)
                {
                    return nodes;
                }

                var values = new object?[entityType.FieldsByOrdinal.Length];

                foreach (var (level, entries) in index.Tree.Nodes())
                {
                    var items = new List<(object?[] Key, object? Entity)>(entries.Count);

                    foreach (var entry in entries)
                    {
                        var key = new object?[definition.Parts.Count];
                        object? entity = null;

                        index.Layout.ReadValues(parts.Arena, entry, key);

                        if (level == 0)
                        {
                            var root = parts.Roots.Entry(IndexLayout.IdentityOf(entry)).Root;

                            RootValues.Read(new RootReader(parts.Arena, table.Layout!, root), entityType, values);
                            entity = values[entityType.Key.Ordinal];
                        }

                        items.Add((key, entity));
                    }

                    nodes.Add((level, items));
                }

                return nodes;
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Frees every retired thing no read in progress can reach — an old root with its blocks, a replaced table
    // of the primary index, the entry of a deleted root — and returns. It does not wait and takes no timeout:
    // with no read in progress it leaves nothing retired, and what a read in progress can still reach stays
    // retired until a later drain. It is what a test calls before it counts; a write under memory pressure
    // and a close drain in the same way (RetiredMemory.Drain, FreeAfterOperationsAsync). Unavailable before
    // the open and once Faulted, ObjectDisposedException after the close. It runs under the lock of the
    // writes, between two changes of the engine, because only that thread frees (RetiredMemory).
    public void DrainRetired()
    {
        ThrowIfNotReady();
        _barrier.Enter();

        try
        {
            var parts = Part(_parts);

            lock (parts.Writes)
            {
                parts.Retired.Drain();
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next read by key that
    // finds its root in the index runs hold, once, on its own thread and inside the protection of
    // reads, after it has found the root and before it copies it, with the handle of the root entry
    // it found. A test waits inside hold, with a bounded wait, to keep that read inside the
    // protection while the test writes, drains, counts and closes; when hold returns, the read goes
    // on as it would have. Every other read passes the point without running anything. The switch
    // can be armed again while a read it held earlier is still held, so that two reads are held at
    // once. A read when no test has armed the switch pays nothing for it beyond reading one field
    // that is null (ReadEpochs.OfferHold). Unavailable before the open, ObjectDisposedException after
    // the close.
    public void HoldNextRead(Action<RootHandle> hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _epochs.HoldNext(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next growth of the
    // primary index of the entity type runs hold, once, on the thread of the write that grows it and
    // inside the lock of the writes, after it has published the grown table and before it retires the
    // old one, while a step that entered before may still walk the old table and a step that enters
    // now walks the new one. A test waits inside hold, with a bounded wait, to keep the growth there
    // while it takes steps of an enumeration, or counts the growths; it neither writes nor counts the
    // counters meanwhile, since the lock of the writes is held. When hold returns the growth goes on
    // as it would have. A growth when no test has armed the switch pays one load of a field that is
    // null, and a read pays nothing. The entity type has to be one the engine stores. Unavailable
    // before the open, ObjectDisposedException after the close.
    public void HoldNextGrowth(LatticeEntityType entityType, Action hold)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        Part(_parts).Operations[entityType].Table.HoldNextGrowth(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next write that the arena
    // refused twice runs hold, once, at the first wait of its resolution, outside every lock, when it
    // knows it has to wait and before it waits, so that a test can tell that the write waits instead of
    // giving it a window of time. A refused write is resolved alone: while it holds back the preparation
    // of later writes it waits for the writes prepared and not published yet, then for the reads that
    // hold retired memory (WriteCoordinator, "The refusal"); the switch runs at the first of these waits
    // that the write has to make. On a child, which admits no write, the switch runs for the next
    // transaction its arena refused twice, once its applier knows it waits for the reads that hold
    // retired memory and before it waits (Child.Loading.ReceivedChanges.Apply). When hold returns the write or
    // the transaction waits as it would have. Only a write or a transaction the arena refused loads the
    // switch: no other write and no read pays anything for it (RetiredMemory.RunWaitSwitch). Unavailable
    // before the open, ObjectDisposedException after the close.
    public void HoldNextRefusedWriteWait(Action hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        Part(_parts).Retired.ArmWaitSwitch(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next pass of a write through
    // the point throws InvalidOperationException with a message that names the point; tests only. Both points
    // lie after the storage step, and a failure at either puts the instance into Faulted
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
