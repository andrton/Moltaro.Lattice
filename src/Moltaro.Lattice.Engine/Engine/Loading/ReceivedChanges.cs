using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Loading;

// What a child does with the states of keys it receives from its master: the roots of its load and the
// effects of the transactions of its stream, by one rule (brief of Epic 1, sprint S11; the planner's
// decision of 17 November 2026 after the study, point 7):
//
//   A state of a key at sequence s is skipped when the child holds that key with a last mutation not
//   earlier than s; otherwise a Write inserts or replaces, and a Delete removes the key if it is held.
//
// The child addresses an entity by the key in the root, never by a slot of the master, so a key copied
// twice in a load, from two slots, a key the capture copied after a write the stream carries, and a slot
// the master gave to another key, are all resolved by the rule. Why the rule ends every key at its state
// at R: the copy of a key and the transactions after S are applied in their order, every copy before
// every transaction, and a copy is a state of a key between S and R. A key no transaction of (S, R]
// touches has its state of S in every copy of it, or no copy when it held none; a key one touches ends
// with the state of the last of them, which the rule applies or finds already held, since no copy is
// later than R. A delete removes what it finds older than itself, and no copy can come after it to bring
// the key back. So the capture needs no mark of a deletion and no slot kept from reuse (the owner's
// amendment of Q08 of 17 November 2026).
//
// One changing thread: the arena of a child, as of a master, is changed only by whoever holds the lock of
// the writes. A root of the load (Place) and a transaction of the stream (Apply) are prepared and published
// under the child's lock of the writes, as a write is; no second way of changing an arena is built, and no
// storage step runs. The lock is held for synchronous work in memory, never across a receive. Each of them
// passes the barrier of the close, as a write does, so a close frees the arena only once no placement and
// no application is in progress, and one after the close is refused with ObjectDisposedException.
//
// How. Under the lock of the writes, as a write on a master is: each change is checked and prepared
// first — the root placed in prepared blocks for the lifetime it goes to, with the room its publication
// needs reserved, in the index for an insert and in the retired list for a replace or a delete — and
// nothing is published until every change of the transaction is prepared (Publish); a change that is not
// valid, or that the arena refuses, abandons every change prepared since (Abandon), so a transaction is
// applied whole or not at all. Then everything prepared is published through the stores of the write path
// (Publish, Writes.WriteExecutor) with the sequence of the master as its last mutation, so the revision of
// every entity on the child is its revision on the master. The key of a root is read from the checked
// bytes before anything is taken for it (Replication.Load.BlockImport), so a skipped state takes nothing
// of the arena, the root table, the index or the retired list; for a String key its text is decoded into
// a string first, the one allocation a skipped state makes beside a growth of the list that remembers the
// keys a transaction names. A replaced root is placed for the lifetime of the entity it replaces.
//
// Publication, and what a read by key sees. A transaction at sequence s is published in three steps, the
// stores of the writes of a master split where a master publishes its position (Publish):
// 1. every insert is installed, its entry and then its entry of the index, with the stamp s; every replace
//    stores the mark of a write in progress and then its new root; every delete stores the mark and then
//    the state Deleted;
// 2. the position of the child becomes s, once;
// 3. every replace and delete stores its stamp s; then every delete removes its entry of the index; what
//    they replaced or deleted is retired.
// So a read never returns a state at a position at which the master did not have it
// (Entities.EntityTable, "The position of an answer"). The position of the child is s − 1 until step 2.
// A read that finds an inserted key reads its stamp s, at s; one that misses it reads s − 1 before its
// lookup. A read of a replaced or deleted key returns its old state only if the mark was not stored yet
// when it loaded the stamp the second time, and the mark is stored before the position, so it then read the
// position s − 1 or earlier; a read that finds the mark backs off until step 3, and one that loads the
// stamp s reads the new root, or the state Deleted at s, with the position s or later; a read that misses
// a deleted key does so after step 3, at s or later. With one effect this is the order of the write of a
// master. Once step 1 has begun nothing can fail: every store goes to what the preparation took and
// reserved, and nothing allocates. Should one fail anyway, a defect, the reads are marked faulted at once,
// under the lock (ReadEpochs.Fault, which takes no lock), so that a read that backs off
// on a mark gives up with Unavailable instead of waiting for a stamp that will never come, as on a master;
// once it has left the lock it faults the rest of the instance, its state and its clock (the action
// given to the constructor; EngineDatabase, the order of the locks).
//
// Memory. A root or a transaction the arena refuses follows the rule of a write refused for memory: what
// is retired is freed and it is tried once more. For a root of the load that is all: before the
// child is Ready no read can hold retired memory, so there is nothing else to wait for, and a root refused
// again cannot be held: the open of the child ends with Unavailable and the reason (the planner's block of
// 17 November 2026, point 9). A transaction refused again waits for the reads that hold retired memory,
// what they held is freed, and it is tried once more; refused then, it cannot be held (Following.Follower).
internal sealed class ReceivedChanges
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ReadEpochs _epochs;

    private readonly WriteExecutor _executor;

    private readonly EntityTables _tables;

    private readonly Lock _writes;

    private readonly CloseBarrier _barrier;

    // The coordinator of the child, which admits no write; it keeps the switch of the tests for a refused
    // write, which a transaction the arena refused runs before its wait for the reads (Apply).
    private readonly WriteCoordinator _coordinator;

    private readonly BlockImport _import;

    // Faults the instance, after the lock, when a publication failed between its stores.
    private readonly Action _fault;

    // The changes prepared and not published yet, in the order they were received.
    private readonly List<Change> _prepared = [];

    // The keys of the states the rule skipped since the last Publish: a skipped state names its key as
    // much as a prepared change does (ThrowIfNamed).
    private readonly List<(EntityTable Table, EntityKey Key)> _skipped = [];

    // The states of keys published, and those the rule skipped, since the instance opened, counted when
    // their transaction or root is published: a Delete of a key the child does not hold counts as
    // skipped. For the report of a catch-up (Following.Follower); written and read by the one thread that
    // applies.
    public long AppliedStates { get; private set; }

    public long SkippedStates { get; private set; }

    // Whether a publication failed between its stores, so that the instance is faulted after the lock (see
    // the class); it stays so.
    private bool _publicationFailed;

    public ReceivedChanges(
        Arena arena,
        RootTable roots,
        RetiredMemory retired,
        PositionClock clock,
        ReadEpochs epochs,
        WriteExecutor executor,
        LatticeSchema schema,
        EntityTables tables,
        Lock writes,
        CloseBarrier barrier,
        WriteCoordinator coordinator,
        Action fault)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _epochs = epochs;
        _executor = executor;
        _tables = tables;
        _writes = writes;
        _barrier = barrier;
        _coordinator = coordinator;
        _fault = fault;
        _import = new BlockImport(arena, schema);
    }

    // Places and publishes a root of the load of the entity type of the table, whose last mutation is given
    // and whose rest the input holds, from its layout version on (RootPlacement), under the lock of the
    // writes and by the rule of a write refused for memory (see the class). The roots received before it
    // are counted for the reason of a root that cannot be held.
    public void Place(EntityTable table, ulong lastMutation, SequenceReader<byte> rest, ulong received)
    {
        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                PlaceUnderTheLock(rest, table, lastMutation, received);
            }
        }
        catch
        {
            // After the lock: the reads were marked faulted under it.
            if (_publicationFailed)
            {
                _fault();
            }

            throw;
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // The end of the load: S, the start of the capture, becomes the sequence of the child (RootPlacement.End).
    public void EndLoad(ulong start)
    {
        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                _clock.Publish(start);
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Applies one transaction whole, by the rule of a write refused for memory (see the class).
    public void Apply(ulong sequence, ReadOnlySequence<byte> effects, CancellationToken cancellationToken)
    {
        _barrier.Enter();

        try
        {
            if (TryApply(sequence, effects))
            {
                return;
            }

            long epoch;
            bool retired;

            lock (_writes)
            {
                _retired.Drain();
            }

            if (TryApply(sequence, effects))
            {
                return;
            }

            lock (_writes)
            {
                epoch = _executor.ReadsToWaitFor(out retired);
            }

            if (retired && epoch != 0)
            {
                _coordinator.RunWaitHold();
                _epochs.WaitForReadsBefore(epoch, cancellationToken);

                lock (_writes)
                {
                    _retired.Drain();
                }

                if (TryApply(sequence, effects))
                {
                    return;
                }
            }

            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The memory limit of this child cannot hold the transaction {sequence} of its master.");
        }
        catch
        {
            // After the lock, which TryApply takes and leaves: the reads were marked faulted under it.
            if (_publicationFailed)
            {
                _fault();
            }

            throw;
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // The root placed by the rule of a write refused for memory (see the class).
    private void PlaceUnderTheLock(SequenceReader<byte> start, EntityTable table, ulong lastMutation, ulong received)
    {
        var reader = start;

        if (TryPrepareWrite(table, lastMutation, ref reader))
        {
            Publish(position: null);

            return;
        }

        _retired.Drain();
        reader = start;

        if (TryPrepareWrite(table, lastMutation, ref reader))
        {
            Publish(position: null);

            return;
        }

        throw new LatticeException(
            LatticeErrorEnum.Unavailable,
            $"The memory limit of this child cannot hold the database of its master: a root of {table.EntityType.Name} of {start.Remaining} bytes does not fit beside the {received} roots received before it.");
    }

    // Under the lock of the writes: prepares every effect of the transaction and publishes them, or none of
    // them. False when the arena refused one, with nothing of the transaction left.
    private bool TryApply(ulong sequence, ReadOnlySequence<byte> effects)
    {
        lock (_writes)
        {
            var reader = new SequenceReader<byte>(effects);

            try
            {
                while (!reader.End)
                {
                    if (!TryPrepareEffect(ref reader, sequence))
                    {
                        return false;
                    }
                }
            }
            catch
            {
                Abandon();
                throw;
            }

            Publish(sequence);

            return true;
        }
    }

    // Prepares the next effect of the transaction (Replication.Transactions.TransactionFormat): false when
    // the arena refused it. Each effect is of an entity type the engine stores, and the root of a Write
    // carries the sequence of its transaction as its last mutation (Following.Follower).
    private bool TryPrepareEffect(ref SequenceReader<byte> reader, ulong sequence)
    {
        switch (TransactionFormat.ReadEffect(ref reader, sequence, out var typeId, out var lastMutation, out var root))
        {
            case EffectKindEnum.Write:
                var written = TableOf(typeId, sequence);

                if (lastMutation != sequence)
                {
                    throw Protocol.Invalid($"A Write of the transaction {sequence} carries a root of {written.EntityType.Name} whose last mutation is {lastMutation}.");
                }

                return TryPrepareWrite(written, sequence, ref root);

            case EffectKindEnum.Delete:
                var deleted = TableOf(typeId, sequence);
                var key = deleted.ReadKey(ref reader);

                PrepareDelete(deleted, key, sequence);

                return true;

            default:
                // An Unchanged changes no entity.
                return true;
        }
    }

    // The table of an entity type the engine stores.
    private EntityTable TableOf(uint typeId, ulong sequence) =>
        _tables.TryGetStored(typeId, out var table)
            ? table
            : throw Protocol.Invalid($"The transaction {sequence} names the entity type {typeId}, which the schema of the child does not store.");

    // Under the lock of the writes: checks the root of the entity type of the table that the rest of the
    // input holds, from its layout version on, whose last mutation is given, and prepares its state by
    // the rule (see the class). True when it is prepared or skipped; false when the arena refused it,
    // with every change prepared since the last Publish abandoned. ValidationFailed for a root that is not
    // valid, and for a key an earlier effect of the same transaction names, prepared or skipped; every
    // change prepared since the last Publish is abandoned then as well.
    private bool TryPrepareWrite(EntityTable table, ulong lastMutation, ref SequenceReader<byte> rest)
    {
        var layout = table.Layout!;
        EntityKey key;
        ReceivedRoot received;

        try
        {
            received = _import.Check(ref rest, layout);
            key = KeyOf(table, received);
            ThrowIfNamed(table, key);
        }
        catch
        {
            Abandon();
            throw;
        }

        var held = table.TryFind(key, out var handle);

        if (held && _roots.Entry(handle).LastMutationSequence >= lastMutation)
        {
            _skipped.Add((table, key));

            return true;
        }

        // A new lifetime for a key the child does not hold; the lifetime of the entity for one it holds.
        var inserts = !held;

        if (inserts)
        {
            try
            {
                handle = _roots.Add((uint)table.EntityType.Id);
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        var root = default(ArenaAddress);

        try
        {
            root = _import.Place(received, handle);

            if (root.IsNull)
            {
                GiveBack(table, inserts, handle, root);

                return false;
            }

            if (inserts)
            {
                table.Reserve(key);
            }
            else
            {
                _retired.Reserve();
            }
        }
        catch
        {
            GiveBack(table, inserts, handle, root);
            throw;
        }

        _prepared.Add(new Change(inserts ? ChangeKindEnum.Insert : ChangeKindEnum.Replace, table, key, handle, root, lastMutation));

        return true;
    }

    // Under the lock of the writes: prepares the delete of the key at the sequence by the rule (see the
    // class); nothing when the child does not hold the key, or holds a state not earlier than the delete.
    // ValidationFailed for a key an earlier effect of the same transaction names, prepared or skipped,
    // with every change prepared since the last Publish abandoned.
    private void PrepareDelete(EntityTable table, in EntityKey key, ulong sequence)
    {
        try
        {
            ThrowIfNamed(table, key);
        }
        catch
        {
            Abandon();
            throw;
        }

        if (!table.TryFind(key, out var handle) || _roots.Entry(handle).LastMutationSequence >= sequence)
        {
            _skipped.Add((table, key));

            return;
        }

        try
        {
            _retired.Reserve();
        }
        catch
        {
            Abandon();
            throw;
        }

        _prepared.Add(new Change(ChangeKindEnum.Delete, table, key, handle, default, sequence));
    }

    // Under the lock of the writes: publishes every change prepared, in the three steps of the class, with
    // the position given, or none for a root of a load. Then what is retired is freed as after a write, by
    // this thread.
    private void Publish(ulong? position)
    {
        // Whether a replace or a delete retires what it replaced or deleted.
        var retires = false;

        try
        {
            for (var index = 0; index < _prepared.Count; index++)
            {
                var change = _prepared[index];

                switch (change.Kind)
                {
                    case ChangeKindEnum.Insert:
                        _executor.Install(change.Table, change.Key, change.Handle, change.Root, change.Sequence);
                        break;

                    case ChangeKindEnum.Replace:
                        _prepared[index] = change with { Replaced = _executor.MarkReplace(change.Table.Layout!, change.Handle, change.Root) };
                        break;

                    default:
                        _executor.MarkDelete(change.Handle);
                        break;
                }
            }

            if (position is { } sequence)
            {
                _clock.Publish(sequence);
            }

            foreach (var change in _prepared)
            {
                switch (change.Kind)
                {
                    case ChangeKindEnum.Replace:
                        _executor.StampReplace(change.Table.Layout!, change.Handle, change.Replaced, change.Sequence);
                        retires = true;
                        break;

                    case ChangeKindEnum.Delete:
                        _executor.StampDelete(change.Table, change.Key, change.Handle, change.Sequence);
                        retires = true;
                        break;
                }
            }
        }
        catch
        {
            _publicationFailed = true;
            _epochs.Fault();
            throw;
        }

        AppliedStates += _prepared.Count;
        SkippedStates += _skipped.Count;
        _skipped.Clear();
        _prepared.Clear();
        _retired.AfterWrite(retiredNothing: retires ? 0 : 1);
    }

    // Under the lock of the writes: gives back everything the changes prepared since the last Publish
    // took, in the reverse of their order: no read has reached any of it.
    private void Abandon()
    {
        for (var index = _prepared.Count - 1; index >= 0; index--)
        {
            var change = _prepared[index];

            switch (change.Kind)
            {
                case ChangeKindEnum.Insert:
                    RootStorage.Free(_arena, change.Table.Layout!, change.Root);
                    change.Table.ReleaseReservation();
                    _roots.Release(change.Handle);
                    break;

                case ChangeKindEnum.Replace:
                    RootStorage.Free(_arena, change.Table.Layout!, change.Root);
                    _retired.Release();
                    break;

                default:
                    _retired.Release();
                    break;
            }
        }

        _skipped.Clear();
        _prepared.Clear();
    }

    // The key of a checked root, read where the root holds it and hashed under the seed of this instance.
    private EntityKey KeyOf(EntityTable table, in ReceivedRoot received)
    {
        if (table.KeyKind == KeyKindEnum.Text)
        {
            return table.KeyOfText(_import.Text(received, table.EntityType.Key));
        }

        var fixedPart = _import.FixedPart(received);

        return table.StoredKey(fixedPart);
    }

    // A transaction names each key once: two changes of one key in one transaction would be prepared
    // against the same state. An effect the rule skips names its key as well, so a key named again after
    // it is refused too.
    private void ThrowIfNamed(EntityTable table, in EntityKey key)
    {
        foreach (var change in _prepared)
        {
            if (ReferenceEquals(change.Table, table) && table.KeysEqual(change.Key, key))
            {
                throw NamedTwice(table, key);
            }
        }

        foreach (var (skippedTable, skippedKey) in _skipped)
        {
            if (ReferenceEquals(skippedTable, table) && table.KeysEqual(skippedKey, key))
            {
                throw NamedTwice(table, key);
            }
        }
    }

    private static LatticeException NamedTwice(EntityTable table, in EntityKey key) =>
        new(LatticeErrorEnum.ValidationFailed, $"A transaction received from the master changes the key '{key}' of {table.EntityType.Name} twice.");

    // A write that was not prepared gives back what it took, its root when it was placed and the entry of
    // an insert, which no index maps; then every change prepared before it is abandoned.
    private void GiveBack(EntityTable table, bool inserted, RootHandle handle, ArenaAddress root)
    {
        if (!root.IsNull)
        {
            RootStorage.Free(_arena, table.Layout!, root);
        }

        if (inserted)
        {
            _roots.Release(handle);
        }

        Abandon();
    }

    private enum ChangeKindEnum
    {
        Insert = 1,
        Replace = 2,
        Delete = 3,
    }

    // One change prepared: what it does, to which entity, and what its publication installs; for a replace,
    // the root it replaced, from step 1 of its publication on.
    private readonly record struct Change(ChangeKindEnum Kind, EntityTable Table, EntityKey Key, RootHandle Handle, ArenaAddress Root, ulong Sequence, ArenaAddress Replaced = default);
}
