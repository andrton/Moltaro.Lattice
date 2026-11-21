using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Children;

// What a master gives each child that loads from it and follows it: the master's Hello, its schema in its
// compiled form, the start of a capture, the copy of one root at a time, the end of the capture, and the
// tail of the transactions after its start (docs/design/milestone-1-engine-contract.md, "Replication
// transport and initial loading").
//
// The capture, while the master goes on writing (docs/decisions/replication-and-hosting.md, "Initial
// capture and readiness handoff"). It is registered in one hold of the lock of the writes, which takes S,
// the published sequence, the fence of the slots, the counts of the roots, and the cursor of the child in
// the tail (Register): every write published before that hold is in the state the copy finds, every one
// published after it is appended to the tail for the child (Writes.WriteExecutor), and none falls between
// the two. The copy then walks the slots below the fence: every root live at S lies below it, and a root
// inserted later, in a slot beyond it, arrives through the tail. The copy of a slot may find a state later
// than S, a root deleted since, or another key in a slot given out again: the child applies every copy and
// every transaction after S by one rule, by the key in the root and the sequence of its last mutation, so
// none of these needs anything of the capture (Loading.ReceivedChanges). Nothing is retained for a
// capture: no slot, no deleted root, no mark of a deletion. Once the last root is copied, R is read in a
// hold of the lock of the writes (End): an insert stores the sequence of its entry before it publishes the
// sequence of the database, so a copy can carry a sequence the published clock does not show yet outside
// that lock, and every root copied has a last mutation no later than R. The child applies the
// transactions up to R and is Ready there.
//
// The capture is not a read of the application. It walks the slots of the root table, not a primary
// index and not the public enumeration, so ReadMemoryLimit and ReadTimeLimit do not apply to it and it
// remembers no keys. For each slot it enters the protection of reads, takes the entry as one state the
// way a read does (EntityTable.TryTakeLive), copies a live root with every block it owns into the buffer
// of the connection, and leaves the protection; the connection sends afterwards. So it never holds a slot
// of the epoch table across a send or between two roots, and the root it copies stays allocated while it
// copies: a root that a write retired is freed only after the reads that could reach it have left. The
// copy of one root is coherent without any gate: a published root is never changed in place
// (docs/decisions/memory-and-access.md).
//
// The lock of the writes is the database's; every member here that takes it holds it for work in memory
// only and takes no other lock inside it.
internal sealed class Capture
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly ReadEpochs _epochs;

    private readonly PositionClock _clock;

    private readonly Lock _writes;

    private readonly EntityTables _tables;

    public Capture(
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        PositionClock clock,
        Lock writes,
        DeliveryTail tail,
        EntityTables tables,
        HelloBody hello,
        byte[] savedSchema)
    {
        _arena = arena;
        _roots = roots;
        _epochs = epochs;
        _clock = clock;
        _writes = writes;
        Tail = tail;
        _tables = tables;
        Hello = hello;
        SavedSchema = savedSchema;
    }

    // The Hello of the master, which a child it accepts receives as its HelloReply.
    public HelloBody Hello { get; }

    // The schema in its compiled form (Replication.Load.SchemaTransfer).
    public byte[] SavedSchema { get; }

    // The tail of the transactions, which the senders of the children read without the lock and wait on
    // (DeliveryTail.WaitForMoreAsync).
    public DeliveryTail Tail { get; }

    // Registers the capture of a child, in one hold of the lock of the writes, and returns what it starts
    // from (see the class). Unavailable once the database faulted.
    public CaptureStart Register()
    {
        lock (_writes)
        {
            if (_epochs.IsFaulted)
            {
                throw ReadEpochs.FaultedRefusal();
            }

            var captureStart = _clock.Sequence;

            return new CaptureStart(captureStart, _roots.SlotExtent, [.. _tables.InSchemaOrder.Select(table => (long)table.Count)], Tail.Subscribe(captureStart));
        }
    }

    // Copies the root of the slot into the buffer when the slot holds a live root (see the class): its
    // type id and the sequence of its last mutation, little-endian, then what BlockExport writes. False,
    // with the buffer as it was, when the slot holds none. ObjectDisposedException once the database is
    // closed, and Unavailable once it is faulted: the protection of reads refuses to be entered. The token,
    // the close of the master, ends a wait for a slot of the protection, as it does for a read with a
    // token (ReadEpochs.Enter), with OperationCanceledException.
    public bool TryCopy(uint slot, TransferBuffer buffer, CancellationToken cancellationToken)
    {
        var protection = _epochs.Enter(cancellationToken);

        try
        {
            ref var entry = ref _roots.EntryOfSlot(slot);

            if (!EntityTable.TryTakeLive(ref entry, _epochs, _clock, out var root, out var lastMutation))
            {
                return false;
            }

            // The type id of a lifetime found live, which stays that lifetime while the protection is held.
            var typeId = entry.TypeId;

            RootEnvelope.Write(buffer, typeId, lastMutation);
            BlockExport.Write(buffer, _arena, _tables.Stored(typeId).Layout!, root);

            return true;
        }
        finally
        {
            _epochs.Exit(protection);
        }
    }

    // Ends the copy of the capture of the cursor, in one hold of the lock of the writes, and returns R, the
    // published sequence read in it (see the class). The tail stays held for the child from S.
    public ulong End(DeliveryCursor cursor)
    {
        lock (_writes)
        {
            Tail.EndCapture(cursor);

            return _clock.Sequence;
        }
    }

    // The child of the cursor has applied everything it received up to the sequence (DeliveryTail
    // .Acknowledge): false for a sequence no child can have applied.
    public bool Acknowledge(DeliveryCursor cursor, ulong applied)
    {
        lock (_writes)
        {
            return Tail.Acknowledge(cursor, applied);
        }
    }

    // The connection of the cursor has ended, whatever the step: nothing is held for it any more.
    public void Unregister(DeliveryCursor cursor)
    {
        lock (_writes)
        {
            Tail.Unsubscribe(cursor);
        }
    }
}
