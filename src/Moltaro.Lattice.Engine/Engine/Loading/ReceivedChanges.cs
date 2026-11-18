using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
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
// a string first, the one allocation a skipped state makes. A replaced root is placed for the lifetime of
// the entity it replaces.
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
// reserved, and nothing allocates. Should one fail anyway, a defect, the instance faults (the action given
// to the constructor), so that a read that backs off on a mark gives up with Unavailable instead of waiting
// for a stamp that will never come, as on a master.
//
// Memory. A root the arena refuses returns false: the caller applies the rule of a write refused for
// memory before it gives up (RootPlacement, Following.Follower).
internal sealed class ReceivedChanges
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly WriteExecutor _executor;

    private readonly BlockImport _import;

    // Faults the instance, when a publication fails between its stores (see the class).
    private readonly Action _fault;

    // The changes prepared and not published yet, in the order they were received.
    private readonly List<Change> _prepared = [];

    // The states the rule skipped since the last Publish.
    private int _skipped;

    // The states of keys published, and those the rule skipped, since the instance opened, counted when
    // their transaction or root is published: a Delete of a key the child does not hold counts as
    // skipped. For the report of a catch-up (Following.Follower); written and read by the one thread that
    // applies.
    public long AppliedStates { get; private set; }

    public long SkippedStates { get; private set; }

    public ReceivedChanges(Arena arena, RootTable roots, WriteExecutor executor, LatticeSchema schema, Action fault)
    {
        _arena = arena;
        _roots = roots;
        _executor = executor;
        _fault = fault;
        _import = new BlockImport(arena, schema);
    }

    // Under the lock of the writes: checks the root of the entity type of the table that the rest of the
    // input holds, from its layout version on, whose last mutation is given, and prepares its state by
    // the rule (see the class). True when it is prepared or skipped; false when the arena refused it,
    // with every change prepared since the last Publish abandoned. ValidationFailed for a root that is not
    // valid, and for a key an earlier change of the same transaction names; every change prepared since
    // the last Publish is abandoned then as well.
    public bool TryPrepareWrite(EntityTable table, ulong lastMutation, ref SequenceReader<byte> rest)
    {
        var layout = table.Layout!;
        EntityKey key;
        ReceivedRoot received;

        try
        {
            received = _import.Check(ref rest, layout);
            key = KeyOf(table, received);
            ThrowIfPrepared(table, key);
        }
        catch
        {
            Abandon();
            throw;
        }

        var held = table.TryFind(key, out var handle);

        if (held && _roots.Entry(handle).LastMutationSequence >= lastMutation)
        {
            _skipped++;

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
                _executor.ReserveRetirement();
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
    // ValidationFailed for a key an earlier change of the same transaction names, with every change
    // prepared since the last Publish abandoned.
    public void PrepareDelete(EntityTable table, in EntityKey key, ulong sequence)
    {
        try
        {
            ThrowIfPrepared(table, key);
        }
        catch
        {
            Abandon();
            throw;
        }

        if (!table.TryFind(key, out var handle) || _roots.Entry(handle).LastMutationSequence >= sequence)
        {
            _skipped++;

            return;
        }

        try
        {
            _executor.ReserveRetirement();
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
    public void Publish(ulong? position)
    {
        try
        {
            for (var index = 0; index < _prepared.Count; index++)
            {
                var change = _prepared[index];

                switch (change.Kind)
                {
                    case ChangeKindEnum.Insert:
                        _executor.InstallReceived(change.Table, change.Key, change.Handle, change.Root, change.Sequence);
                        break;

                    case ChangeKindEnum.Replace:
                        _prepared[index] = change with { Replaced = _executor.MarkReceivedReplace(change.Table, change.Handle, change.Root) };
                        break;

                    default:
                        _executor.MarkReceivedDelete(change.Handle);
                        break;
                }
            }

            if (position is { } sequence)
            {
                _executor.PublishPosition(sequence);
            }

            foreach (var change in _prepared)
            {
                switch (change.Kind)
                {
                    case ChangeKindEnum.Replace:
                        _executor.StampReceivedReplace(change.Table, change.Handle, change.Replaced, change.Sequence);
                        break;

                    case ChangeKindEnum.Delete:
                        _executor.StampReceivedDelete(change.Table, change.Key, change.Handle, change.Sequence);
                        break;
                }
            }
        }
        catch
        {
            _fault();
            throw;
        }

        AppliedStates += _prepared.Count;
        SkippedStates += _skipped;
        _skipped = 0;
        _prepared.Clear();
        _executor.AfterReceived();
    }

    // Under the lock of the writes: gives back everything the changes prepared since the last Publish
    // took, in the reverse of their order: no read has reached any of it.
    public void Abandon()
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
                    _executor.ReleaseRetirement();
                    break;

                default:
                    _executor.ReleaseRetirement();
                    break;
            }
        }

        _skipped = 0;
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
    // against the same state.
    private void ThrowIfPrepared(EntityTable table, in EntityKey key)
    {
        foreach (var change in _prepared)
        {
            if (ReferenceEquals(change.Table, table) && table.KeysEqual(change.Key, key))
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"A transaction received from the master changes the key '{key}' of {table.EntityType.Name} twice.");
            }
        }
    }

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
