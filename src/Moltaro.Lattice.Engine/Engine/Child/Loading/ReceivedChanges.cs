using System.Buffers;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;

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
// first — the root placed in prepared blocks for the lifetime it goes to —, then what the publication of
// all of them needs is reserved together (Entities.ChangePublisher.TryReserve): the nodes of the secondary
// indexes they change, each index once, room in the primary index for each insert and a place in the
// retired list for each replace or delete; the publisher keeps the secondary indexes, which a child builds
// while it loads and keeps while it follows, as a master does. Nothing is published until every change of
// the transaction is prepared and reserved (Publish); a change that is not valid, a reservation that is
// refused, and an arena that refuses a root give back every change prepared since (GiveBack), so a
// transaction is applied whole or not at all. Then everything prepared is published at once by the
// publisher of the write path (Entities.ChangePublisher), with the sequence of the master as its stamp, so
// the revision of every entity on the child is its revision on the master. The key of a root
// is read from the checked bytes before anything is taken for it (Replication.Load.BlockImport), so a
// skipped state takes nothing of the arena, the root table, the index or the retired list; for a String
// key its text is decoded into a string first, the one allocation a skipped state makes beside a growth of
// the set of the keys a transaction names. A replaced root is placed for the lifetime of the entity it
// replaces.
//
// Each key once. A transaction names each key once: two changes of one key would be prepared against the
// same state. The keys its effects name, prepared or skipped, are one set of (table, key) by the comparison
// of the entity type (Entities.PublicationKeys), as the keys of a batch are on the master, so the check costs
// one look into the set for each effect. The list of the prepared changes and that set serve every
// transaction; after one that named more keys than ChangePublisher.KeptRoom both are let go, so a child does
// not keep the room of the largest transaction it applied.
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
// Unique indexes. During the load and the catch-up nothing is checked: copied roots may reflect different
// published positions (docs/decisions/replication-and-hosting.md), so two of them may hold one value of a
// unique index for a while, and the tree, ordered by key and identity, holds both. At the position the
// child becomes ready at, before it is ready, each unique index is walked once, and a value held twice is a
// protocol failure that fails the open (ValidateUniqueIndexes). From then on a transaction received is checked
// on the state it leaves, as the master checks the changes of one write (Entities.ChangePublisher.TryReserve):
// its changes may exchange values or take one another of them frees, and one that would give a value a
// second owner gives back what the transaction prepared and stops the child as a protocol failure does,
// before anything of it is published, and nothing is repaired: the master cannot have sent it, so the stream
// contradicts what the child holds.
//
// Memory. A root or a transaction the arena refuses follows the rule of a write refused for memory: what
// is retired is freed and it is tried once more, each try and each drain in a hold of the lock of its own
// (TryPlace, TryApply), and the wait for the reads outside it. Between those holds nothing takes memory of
// the arena: during the load no other thread takes the lock, since the members that take it refuse a
// child that is not Ready and the close takes it only after the barrier, and later only a drain or a count
// of the counters can. A try that prepares everything tops the standing reserves of the indexes up before
// it publishes, and the last try before a refusal is made after every index gave back what it holds above
// the bound of the changes waiting, with no top-up, so that the reserves are never the reason of a refusal
// (docs/design/ordered-index.md, "The standing reserve"); a reservation that is refused takes nothing, and
// what the transaction prepared is given back (GiveBack). For a root of the load the second try is the
// last: before the child is Ready
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

    // Faults the instance after the lock when a live transaction diverges or publication fails.
    private readonly Action _fault;

    private readonly Action<int, uint, Arena, RootTable>? _beforeRootListGrowth;
    private readonly Action? _beforeScratchAllocation;

    // The changes prepared and not published yet, in the order they were received: not reserved while the
    // effects of their transaction are prepared, and reserved together once every one is (TryReserve).
    private List<PreparedChange>? _prepared = [];

    // The keys the effects named since the last Publish, prepared or skipped (see the class, "Each key once").
    // Both scratch fields are null between attempts after oversized scratch was discarded.
    private PublicationKeys? _keys;

    // The states the rule skipped since the last Publish.
    private int _skipped;

    // The states of keys published, and those the rule skipped, since the instance opened, counted when
    // their transaction or root is published: a Delete of a key the child does not hold counts as
    // skipped. For the report of a catch-up (Following.Follower); written and read by the one thread that
    // applies.
    public long AppliedStates { get; private set; }

    public long SkippedStates { get; private set; }

    // Set after a checked live divergence or a failure during publication. The
    // instance faults once the holder leaves the writing lock.
    private bool _needsFault;

    private MissingUpdateTargets? _missingUpdates;

    // Whether a change received is checked against the unique indexes: from the position the child becomes
    // ready at on (see the class, "Unique indexes").
    private bool _checksUnique;
    private readonly Action<uint>? _beforePreparationMember;

    public ReceivedChanges(
        Arena arena,
        RootTable roots,
        RetiredMemory retired,
        PositionClock clock,
        ReadEpochs epochs,
        EntityTables tables,
        ChangePublisher publisher,
        Lock writes,
        CloseBarrier barrier,
        Action fault,
        Action<int, uint, Arena, RootTable>? beforeRootListGrowth = null,
        Action? beforeScratchAllocation = null,
        Action<uint>? beforePreparationMember = null)
    {
        _arena = arena;
        _roots = roots;
        _retired = retired;
        _clock = clock;
        _epochs = epochs;
        _publisher = publisher;
        _tables = tables;
        _writes = writes;
        _barrier = barrier;
        _fault = fault;
        _beforeRootListGrowth = beforeRootListGrowth;
        _beforeScratchAllocation = beforeScratchAllocation;
        _beforePreparationMember = beforePreparationMember;
        _import = new BlockImport(arena);
        _keys = new PublicationKeys(0, arena.Budget, restoresPreparation: true);
    }

    // Places and publishes a root of the load of the entity type of the table, whose last mutation is given
    // and whose rest the input holds, from its layout version on (RootPlacement), by the rule of a write
    // refused for memory (see the class). The roots received before it are counted for the reason of a root
    // that cannot be held.
    public void Place(EntityTable table, ulong lastMutation, uint member, SequenceReader<byte> rest, ulong received)
    {
        _barrier.Enter();

        try
        {
            if (TryPlace(table, lastMutation, member, rest, topsUp: true))
            {
                return;
            }

            lock (_writes)
            {
                _retired.Drain();
                _publisher.GiveBackReserves();
            }

            if (TryPlace(table, lastMutation, member, rest, topsUp: false))
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
            if (_needsFault)
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

    // At the position the child becomes ready at, before it is ready, once every transaction up to it is
    // applied: each unique index is walked once in order under the lock of the writes, and a value held
    // twice, a key with no null part, is a protocol failure, ValidationFailed, which fails the open with
    // Unavailable; nothing is repaired. From then on every change received is checked (see the class,
    // "Unique indexes").
    public void ValidateUniqueIndexes()
    {
        _barrier.Enter();

        try
        {
            lock (_writes)
            {
                if (_missingUpdates is { Count: > 0 } missing)
                {
                    throw Protocol.Invalid($"The child reached readiness with {missing.Count} missing update targets without a Delete.");
                }

                _missingUpdates?.Dispose();

                _missingUpdates = null;

                foreach (var table in _tables.InSchemaOrder)
                {
                    foreach (var index in table.Indexes)
                    {
                        if (index.IsUnique && index.HoldsAValueTwice())
                        {
                            throw Protocol.Invalid($"The database of the master reached this child with one value of the unique index {index.Definition.Name} of {table.EntityType.Name} held by two entities at the position it becomes ready at.");
                        }
                    }
                }

                _checksUnique = true;
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // An unsuccessful load has no follower left to use the catch-up buffer.
    public void DropMissingUpdates()
    {
        lock (_writes)
        {
            _missingUpdates?.Dispose();
            _missingUpdates = null;
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
    public void Apply(ulong sequence, ReadOnlySequence<byte> effects, CancellationToken cancellationToken) =>
        Apply(sequence, effects, ready: false, retry: false, cancellationToken);

    public LatticeLimitEnum? Apply(ulong sequence, ReadOnlySequence<byte> effects, bool ready, bool retry, CancellationToken cancellationToken)
    {
        _barrier.Enter();

        try
        {
            if (retry)
            {
                // A maintenance turn can make usable room before finishing the safe FIFO head.
                // Keep the reclamation-before-retry order while the follower's wait is asynchronous.
                lock (_writes)
                {
                    _retired.Drain();
                }
            }

            if (ApplyAttempt(sequence, effects, topsUp: true) is null)
            {
                return null;
            }

            long epoch;

            lock (_writes)
            {
                _retired.Drain();
            }

            if (ApplyAttempt(sequence, effects, topsUp: true) is null)
            {
                return null;
            }

            lock (_writes)
            {
                epoch = _retired.ReadsToWaitFor();
            }

            if (epoch != 0)
            {
                _retired.RunWaitSwitch();
                if (!ready)
                {
                    _epochs.WaitForReadsBefore(epoch, cancellationToken);
                }
            }

            lock (_writes)
            {
                _retired.Drain();
                _publisher.GiveBackReserves();
            }

            var pressure = ApplyAttempt(sequence, effects, topsUp: false);
            if (ready || pressure is null)
            {
                return pressure;
            }

            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The memory limit of this child cannot hold the transaction {sequence} of its master.");
        }
        catch
        {
            // After the lock, which TryApply takes and leaves: the reads were marked faulted under it.
            if (_needsFault)
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

    private LatticeLimitEnum? ApplyAttempt(ulong sequence, ReadOnlySequence<byte> effects, bool topsUp)
    {
        try
        {
            return TryApply(sequence, effects, topsUp) ? null : LatticeLimitEnum.Memory;
        }
        catch (BudgetRefusedException exception) when (!_needsFault)
        {
            return exception.Limit;
        }
    }

    // Under the lock of the writes, which it takes: places the root and publishes it, or skips it by the
    // rule, and tops the standing reserves of the indexes up before its publication when topsUp is true
    // (see the class, "Memory"). False when the arena refused it, with nothing of it left. The reader is a
    // copy, so that every try reads the root from its start.
    private bool TryPlace(EntityTable table, ulong lastMutation, uint member, SequenceReader<byte> rest, bool topsUp)
    {
        lock (_writes)
        {
            var budget = _arena.Budget;
            budget?.BeginPreparation();
            var keeps = false;
            try
            {
                PrepareScratch();

                try
                {
                    if (!TryPrepareWrite(table, lastMutation, member, ref rest) || !TryReserve())
                    {
                        GiveBack();

                        return false;
                    }
                }
                catch
                {
                    GiveBack();
                    throw;
                }

                if (topsUp)
                {
                    _publisher.TopUp(CollectionsMarshal.AsSpan(_prepared));
                }

                keeps = true;
                Publish(lastMutation, publishesPosition: false);

                return true;
            }
            finally
            {
                budget?.EndPreparation(keeps);
                _roots.ReleasePermanentDirectories();
                _arena.Texts.EndPreparation(keeps);
            }
        }
    }

    // Under the lock of the writes: prepares every effect of the transaction and publishes them, or none of
    // them, and tops the standing reserves of the indexes up once all are prepared when topsUp is true (see
    // the class, "Memory"). False when the arena refused one, with nothing of the transaction left.
    private bool TryApply(ulong sequence, ReadOnlySequence<byte> effects, bool topsUp)
    {
        lock (_writes)
        {
            var budget = _arena.Budget;
            budget?.BeginPreparation();
            var keeps = false;
            try
            {
                PrepareScratch();

                var reader = new SequenceReader<byte>(effects);
                string? divergence = null;
                var effect = 0u;

                try
                {
                    while (!reader.End)
                    {
                        if (!TryPrepareEffect(ref reader, sequence, ++effect, ref divergence))
                        {
                            GiveBack();

                            return false;
                        }
                    }

                    if (divergence is not null)
                    {
                        // The whole body was valid. A malformed suffix would have
                        // left through the protocol path before faulting the node.
                        _needsFault = true;
                        _epochs.Fault();
                        throw new LatticeException(LatticeErrorEnum.Unavailable, divergence);
                    }

                    _missingUpdates?.Reserve();
                    if (!TryReserve())
                    {
                        GiveBack();

                        return false;
                    }
                }
                catch
                {
                    GiveBack();
                    throw;
                }

                if (topsUp)
                {
                    _publisher.TopUp(CollectionsMarshal.AsSpan(_prepared));
                }

                keeps = true;
                Publish(sequence, publishesPosition: true);

                return true;
            }
            finally
            {
                budget?.EndPreparation(keeps);
                _roots.ReleasePermanentDirectories();
                _arena.Texts.EndPreparation(keeps);
            }
        }
    }

    // Prepares the next effect of the transaction (Replication.Transactions.TransactionFormat): false when
    // the arena refused it. Each effect is of an entity type the engine stores, and the root of a Write
    // carries the sequence of its transaction as its last mutation, which is the stamp of its publication.
    private bool TryPrepareEffect(ref SequenceReader<byte> reader, ulong sequence, uint effect, ref string? divergence)
    {
        switch (TransactionFormat.ReadEffect(ref reader, sequence, out var typeId, out var lastMutation, out var member, out var root))
        {
            case EffectKindEnum.Write:
                _beforePreparationMember?.Invoke(member == 0 ? effect : member);
                var written = TableOf(typeId, sequence);

                if (lastMutation != sequence)
                {
                    throw Protocol.Invalid($"A Write of the transaction {sequence} carries a root of {written.EntityType.Name} whose last mutation is {lastMutation}.");
                }

                return TryPrepareWrite(written, sequence, member, ref root, validateOnly: divergence is not null);

            case EffectKindEnum.Update:
                _beforePreparationMember?.Invoke(member == 0 ? effect : member);
                return TryPrepareUpdate(TableOf(typeId, sequence), sequence, lastMutation, member, ref root, ref divergence);

            case EffectKindEnum.Delete:
                _beforePreparationMember?.Invoke(member == 0 ? effect : member);
                var deleted = TableOf(typeId, sequence);
                var key = deleted.Keys.ReadKey(ref reader);

                return TryPrepareDelete(deleted, key, sequence, validateOnly: divergence is not null);

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
    // the rule (see the class), without reserving anything for it yet. True when it is prepared or skipped;
    // false when the arena refused its root, with nothing of it taken. ValidationFailed for a root that is
    // not valid and for a key an earlier effect of the same transaction names, prepared or skipped. The
    // changes prepared since the last Publish are the caller's to give back.
    private bool TryPrepareWrite(EntityTable table, ulong lastMutation, uint member, ref SequenceReader<byte> rest, bool validateOnly = false)
    {
        var layout = table.Layout;
        var received = _import.Check(ref rest, layout);
        var key = KeyOf(table, received);

        NameOnce(table, key);
        if (validateOnly)
        {
            _import.CheckSkippedCollections(received);
            return true;
        }

        var held = table.TryFind(key, out var handle, out var replaced);

        if (held && _roots.Entry(handle).LastMutationSequence >= lastMutation)
        {
            _import.CheckSkippedCollections(received);
            _skipped++;

            return true;
        }

        // Once the root takes storage, recording the change for rollback must not allocate.
        var prepared = _prepared!;
        var preparedCount = prepared.Count;
        if (preparedCount == prepared.Capacity)
        {
            _beforeRootListGrowth?.Invoke(preparedCount, member, _arena, _roots);
            prepared.EnsureCapacity(preparedCount + 1, _arena.Budget, nameof(ReceivedChanges), restoresPreparation: true);
        }

        // A new lifetime for a key the child does not hold; the lifetime of the entity for one it holds.
        var inserts = !held;

        if (inserts)
        {
            handle = _roots.Add((uint)table.EntityType.Id);
        }

        ArenaAddress root;

        // The root entry of an insert is not in the list of the prepared changes yet, which the caller gives
        // back, so it is released here.
        try
        {
            root = _import.Place(received, handle, out _);
        }
        catch
        {
            ReleaseHandle(inserts, handle);
            throw;
        }

        if (root.IsNull)
        {
            ReleaseHandle(inserts, handle);

            return false;
        }

        prepared.Add(new PreparedChange(inserts ? ChangeKindEnum.Insert : ChangeKindEnum.Replace, table, key, handle, root, replaced) { Member = member });

        return true;
    }

    private bool TryPrepareUpdate(EntityTable table, ulong sequence, ulong predecessor, uint member,
        ref SequenceReader<byte> body, ref string? divergence)
    {
        var key = table.Keys.ReadKey(ref body);
        var budget = _arena.Budget;
        var commands = UpdateCommands.ReadEffect(table.Layout, ref body, budget);
        try
        {
            return TryPrepareUpdate(table, sequence, predecessor, member, key, commands, ref divergence);
        }
        finally
        {
            budget?.Release(commands);
        }
    }

    private bool TryPrepareUpdate(EntityTable table, ulong sequence, ulong predecessor, uint member,
        in EntityKey key, ResolvedCommand[] commands, ref string? divergence)
    {
        NameOnce(table, key);
        if (divergence is not null)
        {
            return true;
        }

        var held = table.TryFind(key, out var handle, out var replaced);
        var mutation = held ? _roots.Entry(handle).LastMutationSequence : 0;
        if (!_checksUnique)
        {
            if (!held)
            {
                (_missingUpdates ??= new MissingUpdateTargets(_arena.MemoryLimit, _arena.Budget)).Add(table, key);
                _skipped++;
                return true;
            }

            if (mutation >= sequence)
            {
                _skipped++;
                return true;
            }
        }

        if (mutation != predecessor)
        {
            Diverge(ref divergence, $"An Update of {table.EntityType.Name} '{key}' at {sequence} requires predecessor {predecessor}, but this child holds {mutation}.");
            return true;
        }

        RootUpdate plan;
        try
        {
            plan = new RootUpdate(_arena, table.Layout, replaced, commands.AsSpan());
        }
        catch (LatticeException exception) when (exception.Error is LatticeErrorEnum.UpdateTargetMissing or LatticeErrorEnum.ValidationFailed)
        {
            Diverge(ref divergence, $"An Update of {table.EntityType.Name} '{key}' at {sequence} contradicts its predecessor. {exception.Message}");
            return true;
        }

        using (plan)
        {
            var prepared = _prepared!;
            prepared.EnsureCapacity(prepared.Count + 1, _arena.Budget, nameof(ReceivedChanges), restoresPreparation: true);
            var root = RootValues.Place(_arena, table.Layout, handle, values: null, out _, out _, plan);
            if (root.IsNull)
            {
                return false;
            }

            prepared.Add(new PreparedChange(ChangeKindEnum.Replace, table, key, handle, root, replaced) { Member = member });
            return true;
        }
    }

    private void Diverge(ref string? divergence, string reason)
    {
        if (!_checksUnique)
        {
            throw Protocol.Invalid(reason);
        }

        divergence = reason;
    }

    // Under the lock of the writes: prepares the delete of the key at the sequence by the rule (see the
    // class); nothing when the child does not hold the key, or holds a state not earlier than the delete.
    // ValidationFailed for a key an earlier effect of the same transaction names, prepared or skipped.
    private bool TryPrepareDelete(EntityTable table, in EntityKey key, ulong sequence, bool validateOnly)
    {
        NameOnce(table, key);
        if (validateOnly)
        {
            return true;
        }

        _missingUpdates?.Delete(table, key);
        if (!table.TryFind(key, out var handle, out var replaced) || _roots.Entry(handle).LastMutationSequence >= sequence)
        {
            _skipped++;

            return true;
        }

        var prepared = _prepared!;
        prepared.EnsureCapacity(prepared.Count + 1, _arena.Budget, nameof(ReceivedChanges), restoresPreparation: true);
        prepared.Add(new PreparedChange(ChangeKindEnum.Delete, table, key, handle, default, replaced));

        return true;
    }

    // Under the lock of the writes, once every change of a transaction or the root of the load is prepared:
    // reserves what their publication needs, together, and checks their values of the unique indexes on the
    // state they leave once the child does (see the class, "Unique indexes"). False when the arena cannot hold
    // the nodes; ValidationFailed for a value a change would give a second owner. Nothing is reserved then,
    // and the changes are the caller's to give back.
    private bool TryReserve()
    {
        var changes = CollectionsMarshal.AsSpan(_prepared);
        var reservation = _publisher.TryReserve(changes, _checksUnique, out var taken);

        if (reservation == ReservationEnum.Reserved)
        {
            return true;
        }

        if (reservation == ReservationEnum.RefusedForMemory)
        {
            return false;
        }

        var failing = changes[taken.Change];

        throw Taken(failing.Table, failing.Key, taken.Index);
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
            _missingUpdates?.Commit();
        }
        catch
        {
            _needsFault = true;
            _epochs.Fault();
            _publisher.OfferFaultSwitch();
            throw;
        }

        AppliedStates += _prepared!.Count;
        SkippedStates += _skipped;
        EndAttempt(published: true);
        _retired.AfterWrite();
    }

    // Under the lock of the writes: gives back what the changes prepared since the last Publish took, none of
    // which is reserved, in the reverse of their order: the new root of an insert or a replace and the root
    // entry of an insert, which no index maps, so that no read has reached any of it.
    private void GiveBack()
    {
        _publisher.GiveBack(CollectionsMarshal.AsSpan(_prepared));
        EndAttempt(published: false);
    }

    // Under the lock of the writes, before an attempt takes ownership of any change.
    // If either allocation fails, the receiver still has no scratch for that attempt.
    private void PrepareScratch()
    {
        if (_prepared is not null)
        {
            return;
        }

        _beforeScratchAllocation?.Invoke();
        var prepared = new List<PreparedChange>();
        var keys = new PublicationKeys(0, _arena.Budget, restoresPreparation: true);
        _prepared = prepared;
        _keys = keys;
    }

    public void ReleaseTransferBacking()
    {
        lock (_writes)
        {
            ReleaseScratch();
            _import.Dispose();
        }
    }

    private void ReleaseScratch()
    {
        var prepared = _prepared;
        var keys = _keys;
        _prepared = null;
        _keys = null;
        if (prepared is not null && _arena.Budget is { } budget)
        {
            budget.Release(BudgetCollections.Items(prepared));
        }

        keys?.Dispose();
    }

    // Under the lock of the writes, after publication or rollback. Drop oversized scratch
    // without allocating. A later attempt obtains its own scratch before it prepares anything.
    // The named keys count the room of both containers because every prepared change names
    // its key. At the limit (ChangePublisher.KeptRoom), 1,024 changes of 96 bytes and 1,931
    // key entries of 56 bytes with their buckets keep about 210 KiB between attempts.
    private void EndAttempt(bool published)
    {
        var keys = _keys!;
        if (keys.Count > ChangePublisher.KeptRoom && (published || _arena.Budget is null))
        {
            ReleaseScratch();
        }
        else
        {
            _prepared!.Clear();
            keys.Clear();
        }

        _skipped = 0;
        _missingUpdates?.EndAttempt();
    }

    // The key of a checked root, read where the root holds it and hashed under the seed of this instance.
    private EntityKey KeyOf(EntityTable table, in ReceivedRoot received)
    {
        if (table.Keys.KeyKind == KeyKindEnum.Text)
        {
            return table.Keys.KeyOfText(_import.TextKey(received));
        }

        return table.Keys.ReadFixedKey(_import.FixedKeyBytes(received));
    }

    // The effect names its key (see the class, "Each key once"), whether the rule then prepares or skips its
    // state, so a key named again after a skipped state is refused too. ValidationFailed for a key an earlier
    // effect of the transaction named.
    private void NameOnce(EntityTable table, in EntityKey key)
    {
        if (!_keys!.TryAdd(table, key))
        {
            throw NamedTwice(table, key);
        }
    }

    // A change received that would give a value of the unique index at the place given a second owner: the
    // master checks every write, so the stream contradicts what this child holds (see the class).
    private static LatticeException Taken(EntityTable table, in EntityKey key, int index) =>
        Protocol.Invalid($"A transaction received from the master gives {table.EntityType.Name} '{key}' a value of the unique index {table.Indexes[index].Definition.Name} that another entity of this child holds.");

    private static LatticeException NamedTwice(EntityTable table, in EntityKey key) =>
        new(LatticeErrorEnum.ValidationFailed, $"A transaction received from the master changes the key '{key}' of {table.EntityType.Name} twice.");

    // Gives back the root entry of an insert that was not prepared, which no index maps.
    private void ReleaseHandle(bool inserted, RootHandle handle)
    {
        if (inserted)
        {
            _roots.Release(handle);
        }
    }
}
