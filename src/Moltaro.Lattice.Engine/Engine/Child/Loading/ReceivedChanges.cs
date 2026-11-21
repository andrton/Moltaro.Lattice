using System.Buffers;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Child.Loading;

// What a child does with the states of keys it receives from its master: the roots of its load and the
// effects of the transactions of its stream, by one rule (docs/decisions/replication-and-hosting.md,
// "Initial capture and readiness handoff"):
//
//   A state of a key at sequence s is skipped when the child holds that key with a last mutation not
//   earlier than s; otherwise a Write inserts or replaces, and a Delete removes the key if it is held.
//
// The child addresses an entity by the key in the root, never by a slot of the master, so a key copied
// twice in a load, from two slots, a key the capture copied after a write the stream carries, and a slot
// the master gave to another key, are all resolved by the rule. Why the rule ends every key at its state
// at the readiness position R, given that every copy is applied before every transaction after the
// capture start S, and that a copy is a state of its key between S and R:
// - A key no transaction of (S, R] touches has its state of S in every copy of it, or no copy when it
//   held none.
// - A key one touches ends with the state of the last of them, which the rule applies or finds already
//   held, since no copy is later than R.
// - A delete removes what it finds older than itself, and no copy can come after it to bring the key
//   back.
// So the capture needs no mark of a deletion and no slot kept from reuse.
//
// One changing thread: the arena of a child, as of a master, is changed only by whoever holds the lock
// of the writes. A root of the load (Place) and a transaction of the stream (Apply) are prepared and
// published under the child's lock of the writes, as a write is; no second way of changing an arena is
// built, and no persistence step runs. The lock is held for synchronous work in memory, never across a
// receive. Each of them passes the barrier of the close, as a write does, so a close frees the arena
// only once no placement and no application is in progress, and one after the close is refused with
// ObjectDisposedException.
//
// How. Under the lock of the writes, as a write on a master is: each change is checked and prepared
// first — the root placed in prepared blocks for the lifetime it goes to, with the room its publication
// needs reserved (Entities.ChangePublisher.TryReserve), the nodes of the secondary indexes it changes, in
// the primary index for an insert and in the retired list for a replace or a delete; the publisher keeps
// the secondary indexes, which a child builds while it loads and keeps while it follows, as a master does
// — and nothing is published until every change of the transaction is prepared
// (Publish); a change that is not valid, or that the arena refuses, abandons every change prepared since
// (Abandon), so a transaction is applied whole or not at all. Then everything prepared is published at
// once by the publisher of the write path (Entities.ChangePublisher), with the sequence of the master as
// its stamp, so the revision of every entity on the child is its revision on the master. The key of a root
// is read from the checked bytes before anything is taken for it (Replication.Load.BlockImport), so a
// skipped state takes nothing of the arena, the root table, the index or the retired list; for a String
// key its text is decoded into a string first, the one allocation a skipped state makes beside a growth of
// the list that remembers the keys a transaction names. A replaced root is placed for the lifetime of the
// entity it replaces.
//
// Publication. A transaction at sequence s is published in the three steps of every publication: each
// change makes its stores before the position, the position of the child becomes s once, then each change
// makes its stores after it, so a read never returns a state at a position at which the master did not
// have it (docs/design/write-path.md, "How a publication is made"). A root of the load is published without
// a position: the end of the load publishes the capture start (EndLoad). Once the first step has begun
// nothing can fail: every store goes to what the preparation took and reserved, and nothing allocates.
// Should one fail anyway, a defect, the reads are marked faulted at once, under the lock (ReadEpochs.Fault,
// which takes no lock), so that a read that backs off on a mark gives up with Unavailable instead of
// waiting for a stamp that will never come, as on a master; once it has left the lock it faults the rest
// of the instance, its state and its clock (the action given to the constructor; EngineDatabase, the order
// of the locks).
//
// Memory. A root or a transaction the arena refuses follows the rule of a write refused for memory: what
// is retired is freed and it is tried once more, each try and each drain in a hold of the lock of its own
// (TryPlace, TryApply), and the wait for the reads outside it. Between those holds nothing takes memory of
// the arena: during the load no other thread takes the lock, since the members that take it refuse a
// child that is not Ready and the close takes it only after the barrier, and later only a drain or a count
// of the counters can. A try that prepares everything tops the standing reserves of the indexes up before
// it publishes, and the last try before a refusal is made after every index gave back what it holds above
// the need, with no top-up, so that the reserves are never the reason of a refusal (Indexes.OrderedTree,
// "The standing reserve"); what an index took for a change that is not published goes back to the arena
// with its withdrawal, not into its reserve (Abandon). For a root of the load the second try is the last: before the child is Ready
// no read can hold retired memory, so there is nothing else to wait for, and a root refused again cannot
// be held: the open of the child ends with Unavailable and the reason. A transaction refused again waits
// for the reads that hold retired memory, what they held is freed, and it is tried a last time; refused
// then, it cannot be held: Apply ends with Unavailable, which fails the open of a child that is not Ready
// yet and ends the following of one that is (Following.Follower).
internal sealed class ReceivedChanges
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly RetiredMemory _retired;

    private readonly PositionClock _clock;

    private readonly ReadEpochs _epochs;

    // Publishes what the child applies with the stores of the write path; it holds no state of its own.
    private readonly ChangePublisher _publisher;

    private readonly EntityTables _tables;

    private readonly Lock _writes;

    private readonly CloseBarrier _barrier;

    private readonly BlockImport _import;

    // Faults the instance, after the lock, when a publication failed between its stores.
    private readonly Action _fault;

    // The changes prepared and not published yet, in the order they were received.
    private readonly List<PreparedChange> _prepared = [];

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
        EntityTables tables,
        Lock writes,
        CloseBarrier barrier,
        Action fault)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _epochs = epochs;
        _publisher = new ChangePublisher(arena, roots, retired, clock, tables);
        _tables = tables;
        _writes = writes;
        _barrier = barrier;
        _fault = fault;
        _import = new BlockImport(arena);
    }

    // Places and publishes a root of the load of the entity type of the table, whose last mutation is given
    // and whose rest the input holds, from its layout version on (RootPlacement), by the rule of a write
    // refused for memory (see the class). The roots received before it are counted for the reason of a root
    // that cannot be held.
    public void Place(EntityTable table, ulong lastMutation, SequenceReader<byte> rest, ulong received)
    {
        _barrier.Enter();

        try
        {
            if (TryPlace(table, lastMutation, rest, topsUp: true))
            {
                return;
            }

            lock (_writes)
            {
                _retired.Drain();
                _publisher.GiveBackReserves();
            }

            if (TryPlace(table, lastMutation, rest, topsUp: false))
            {
                return;
            }

            throw new LatticeException(
                LatticeErrorEnum.Unavailable,
                $"The memory limit of this child cannot hold the database of its master: a root of {table.EntityType.Name} of {rest.Remaining} bytes does not fit beside the {received} roots received before it.");
        }
        catch
        {
            // After the lock, which TryPlace takes and leaves: the reads were marked faulted under it.
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

    // The end of the load: the capture start S becomes the sequence of the child (RootPlacement.End).
    public void EndLoad(ulong captureStart)
    {
        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                _clock.Publish(captureStart);
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
            if (TryApply(sequence, effects, topsUp: true))
            {
                return;
            }

            long epoch;

            lock (_writes)
            {
                _retired.Drain();
            }

            if (TryApply(sequence, effects, topsUp: true))
            {
                return;
            }

            lock (_writes)
            {
                epoch = _retired.ReadsToWaitFor();
            }

            if (epoch != 0)
            {
                _retired.RunWaitSwitch();
                _epochs.WaitForReadsBefore(epoch, cancellationToken);
            }

            lock (_writes)
            {
                _retired.Drain();
                _publisher.GiveBackReserves();
            }

            if (TryApply(sequence, effects, topsUp: false))
            {
                return;
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

    // Under the lock of the writes, which it takes: places the root and publishes it, or skips it by the
    // rule, and tops the standing reserves of the indexes up before its publication when topsUp is true
    // (see the class, "Memory"). False when the arena refused it, with nothing of it left. The reader is a
    // copy, so that every try reads the root from its start.
    private bool TryPlace(EntityTable table, ulong lastMutation, SequenceReader<byte> rest, bool topsUp)
    {
        lock (_writes)
        {
            if (!TryPrepareWrite(table, lastMutation, ref rest))
            {
                return false;
            }

            if (topsUp)
            {
                _publisher.TopUp(CollectionsMarshal.AsSpan(_prepared));
            }

            Publish(lastMutation, publishesPosition: false);

            return true;
        }
    }

    // Under the lock of the writes: prepares every effect of the transaction and publishes them, or none of
    // them, and tops the standing reserves of the indexes up once all are prepared when topsUp is true (see
    // the class, "Memory"). False when the arena refused one, with nothing of the transaction left.
    private bool TryApply(ulong sequence, ReadOnlySequence<byte> effects, bool topsUp)
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

            if (topsUp)
            {
                _publisher.TopUp(CollectionsMarshal.AsSpan(_prepared));
            }

            Publish(sequence, publishesPosition: true);

            return true;
        }
    }

    // Prepares the next effect of the transaction (Replication.Transactions.TransactionFormat): false when
    // the arena refused it. Each effect is of an entity type the engine stores, and the root of a Write
    // carries the sequence of its transaction as its last mutation, which is the stamp of its publication.
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
                var key = deleted.Keys.ReadKey(ref reader);

                return TryPrepareDelete(deleted, key, sequence);

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
        PreparedChange change;

        try
        {
            root = _import.Place(received, handle, out _);

            if (root.IsNull)
            {
                GiveBack(table, inserts, handle, root);

                return false;
            }

            change = new PreparedChange(inserts ? ChangeKindEnum.Insert : ChangeKindEnum.Replace, table, key, handle, root);

            if (!_publisher.TryReserve(ref change))
            {
                GiveBack(table, inserts, handle, root);

                return false;
            }
        }
        catch
        {
            GiveBack(table, inserts, handle, root);
            throw;
        }

        _prepared.Add(change);

        return true;
    }

    // Under the lock of the writes: prepares the delete of the key at the sequence by the rule (see the
    // class); nothing when the child does not hold the key, or holds a state not earlier than the delete.
    // False when the arena cannot hold the nodes its indexes need, with every change prepared since the
    // last Publish abandoned. ValidationFailed for a key an earlier effect of the same transaction names,
    // prepared or skipped, with every change prepared since the last Publish abandoned.
    private bool TryPrepareDelete(EntityTable table, in EntityKey key, ulong sequence)
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

            return true;
        }

        var change = new PreparedChange(ChangeKindEnum.Delete, table, key, handle, default);
        bool reserved;

        try
        {
            reserved = _publisher.TryReserve(ref change);
        }
        catch
        {
            Abandon();
            throw;
        }

        if (!reserved)
        {
            Abandon();

            return false;
        }

        _prepared.Add(change);

        return true;
    }

    // Under the lock of the writes: publishes every change prepared, in the three steps of a publication,
    // each with the stamp given, which becomes the position of the child unless they are a root of a load
    // (see the class). Then what is retired is freed as after a write, by this thread.
    private void Publish(ulong stamp, bool publishesPosition)
    {
        try
        {
            var changes = CollectionsMarshal.AsSpan(_prepared);

            _publisher.PublishBeforePosition(changes, stamp);
            _publisher.PublishPositionAndAfter(changes, stamp, publishesPosition);
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
        _retired.AfterWrite();
    }

    // Under the lock of the writes: gives back everything the changes prepared since the last Publish
    // took, in the reverse of their order: no read has reached any of it.
    private void Abandon()
    {
        _publisher.Release(CollectionsMarshal.AsSpan(_prepared));
        _skipped.Clear();
        _prepared.Clear();
    }

    // The key of a checked root, read where the root holds it and hashed under the seed of this instance.
    private EntityKey KeyOf(EntityTable table, in ReceivedRoot received)
    {
        if (table.Keys.KeyKind == KeyKindEnum.Text)
        {
            return table.Keys.KeyOfText(_import.Text(received, table.EntityType.Key));
        }

        var fixedPart = _import.FixedPart(received);

        return table.Keys.StoredKey(fixedPart);
    }

    // A transaction names each key once: two changes of one key in one transaction would be prepared
    // against the same state. An effect the rule skips names its key as well, so a key named again after
    // it is refused too.
    private void ThrowIfNamed(EntityTable table, in EntityKey key)
    {
        foreach (var change in _prepared)
        {
            if (ReferenceEquals(change.Table, table) && table.Keys.KeysEqual(change.Key, key))
            {
                throw NamedTwice(table, key);
            }
        }

        foreach (var (skippedTable, skippedKey) in _skipped)
        {
            if (ReferenceEquals(skippedTable, table) && table.Keys.KeysEqual(skippedKey, key))
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
}
