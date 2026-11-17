using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Children;

// What a master gives each child that loads from it (brief of Epic 1, sprint S10; the planner's block of
// 17 November 2026, points 3 and 6): the master's Hello, its schema in its compiled form, the
// registration of a child, and the copy of one root at a time.
//
// The capture is not a read of the application. It walks the slots of the root table, not a primary
// index and not the public enumeration, so ReadMemoryLimit and ReadTimeLimit do not apply to it and it
// remembers no keys. For each slot it enters the protection of reads, takes the entry as one state the
// way a read does (EntityTable.TryTakeLive), copies a live root with every block it owns into the buffer
// of the connection, and leaves the protection; the connection sends afterwards. So it never holds a slot
// of the epoch table across a send or between two roots, and the root it copies stays allocated while it
// copies: a root that a write retired is freed only after the reads that could reach it have left.
//
// No write is published while a child is registered (Writes.WriteCoordinator, "Children"), so the roots
// below the extent of the root table that the registration took are the whole database at its sequence,
// and every entry the walk reads stays as the last write before the registration left it. The entry of a
// slot is reached by its number, without an index, and a live entry keeps its entity type for its
// lifetime. A capture while the master keeps writing, with its fence and its watermarks, is S11.
internal sealed class Capture
{
    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly ReadEpochs _epochs;

    private readonly PositionClock _clock;

    private readonly WriteCoordinator _coordinator;

    // The tables of the entity types the engine stores, by the identity of their entity type, and every
    // table in the order of the schema.
    private readonly Dictionary<uint, EntityTable> _byTypeId;

    private readonly EntityTable[] _inSchemaOrder;

    public Capture(
        Arena arena,
        RootTable roots,
        ReadEpochs epochs,
        PositionClock clock,
        WriteCoordinator coordinator,
        EntityTable[] inSchemaOrder,
        HelloBody hello,
        byte[] savedSchema)
    {
        _arena = arena;
        _roots = roots;
        _epochs = epochs;
        _clock = clock;
        _coordinator = coordinator;
        _inSchemaOrder = inSchemaOrder;
        _byTypeId = inSchemaOrder.Where(table => table.Layout is not null).ToDictionary(table => (uint)table.EntityType.Id);
        Hello = hello;
        SavedSchema = savedSchema;
    }

    // The Hello of the master, which a child it accepts receives as its HelloReply.
    public HelloBody Hello { get; }

    // The schema in its compiled form (Replication.Load.SchemaTransfer).
    public byte[] SavedSchema { get; }

    // Registers a child: from now on the master refuses every write until Unregister, and what the
    // capture starts from is taken in the same hold of the lock of the writes. Unavailable once the
    // database faulted.
    public CaptureStart Register() => _coordinator.RegisterChild(() => new CaptureStart(
        _clock.Sequence,
        _roots.SlotExtent,
        [.. _inSchemaOrder.Select(table => (long)table.Count)]));

    public void Unregister() => _coordinator.UnregisterChild();

    // Copies the root of the slot into the buffer when the slot holds a live root (see the class): its
    // type id and the sequence of its last mutation, little-endian, then what BlockExport writes. False,
    // with the buffer as it was, when the slot holds none. ObjectDisposedException once the database is
    // closed, and Unavailable once it is faulted: the protection of reads refuses to be entered.
    public bool TryCopy(uint slot, TransferBuffer buffer)
    {
        var protection = _epochs.Enter();

        try
        {
            ref var entry = ref _roots.EntryOfSlot(slot);

            if (!EntityTable.TryTakeLive(ref entry, _epochs, _clock, out var root, out var lastMutation))
            {
                return false;
            }

            var typeId = entry.TypeId;
            var envelope = buffer.GetSpan(sizeof(uint) + sizeof(ulong));

            BinaryPrimitives.WriteUInt32LittleEndian(envelope, typeId);
            BinaryPrimitives.WriteUInt64LittleEndian(envelope[sizeof(uint)..], lastMutation);
            buffer.Advance(sizeof(uint) + sizeof(ulong));

            BlockExport.Write(buffer, _arena, _byTypeId[typeId].Layout!, root);

            return true;
        }
        finally
        {
            _epochs.Exit(protection);
        }
    }
}
