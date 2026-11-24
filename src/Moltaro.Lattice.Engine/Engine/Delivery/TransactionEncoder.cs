using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Delivery;

// Writes one committed transaction of a master into the tail it delivers to its children (DeliveryTail),
// as the bytes that travel (Replication.Transactions.TransactionFormat), once, in the hold of the lock of
// the writes that publishes it (docs/design/milestone-1-engine-contract.md, "Replication transport and
// initial loading"). A transaction is one write: one effect for each change it publishes, in their order,
// the change of a single write or the changes of the members of a write batch. An insert, a full
// replace or an upsert carries the root it publishes. A partial update carries its predecessor
// and effective commands. Both carry the original member number. A delete carries its key. A write
// without changes advances only its position. The first envelope and the header share one span;
// every later envelope also fits one span. Values and keys can continue across blocks. The caller
// appends the encoded transaction after the stores of the write (DeliveryTail.Commit).
internal static class TransactionEncoder
{
    // The changes and any sparse effects prepared by the write. The whole
    // roots used for full effects stay stable under the writing lock.
    public static void Write(DeliveryTail tail, ulong sequence, ReadOnlySpan<PreparedChange> changes, Arena arena, PreparedUpdates? updates = null)
    {
        var effects = 0L;

        foreach (ref readonly var change in changes)
        {
            if (updates?.EffectOf(change.Member) is { } update)
            {
                effects += TransactionFormat.UpdateEnvelopeSize + change.Table.Keys.KeyLength(change.Key) + update.CommandBytes;
            }
            else
            {
                effects += change.Kind == ChangeKindEnum.Delete
                    ? TransactionFormat.DeleteEnvelopeSize + change.Table.Keys.KeyLength(change.Key)
                    : TransactionFormat.WriteEnvelopeSize + RootLength(change, arena);
            }
        }

        tail.Begin(sequence, effects);

        var header = TransactionFormat.HeaderSize;

        foreach (ref readonly var change in changes)
        {
            if (updates?.EffectOf(change.Member) is { } update)
            {
                WriteUpdate(tail, sequence, effects, header, change, update);
            }
            else if (change.Kind == ChangeKindEnum.Delete)
            {
                WriteDelete(tail, sequence, effects, header, change);
            }
            else
            {
                WriteRoot(tail, sequence, effects, header, change, arena);
            }

            header = 0;
        }
    }

    private static void WriteUpdate(DeliveryTail tail, ulong sequence, long effects, int header, in PreparedChange change, in SparseChange update)
    {
        var start = header + TransactionFormat.UpdateEnvelopeSize;
        var bytes = tail.GetSpan(start);
        if (header != 0)
        {
            TransactionFormat.WriteHeader(bytes, sequence, effects);
        }

        var table = change.Table;
        var length = TransactionFormat.UpdateHeadSize + table.Keys.KeyLength(change.Key) + update.CommandBytes;
        TransactionFormat.WriteEnvelopeOfUpdate(bytes[header..], length, (uint)table.EntityType.Id, change.Member, update.BaseSequence);
        tail.Advance(start);
        table.Keys.WriteKey(change.Key, tail);
        UpdateCommands.Write(tail, table.Layout.Structure, update.Commands);
    }

    // A Write of the root of the change, after the header of the transaction when header is its size.
    private static void WriteRoot(DeliveryTail tail, ulong sequence, long effects, int header, in PreparedChange change, Arena arena)
    {
        var layout = change.Table.Layout;
        var start = header + TransactionFormat.WriteEnvelopeSize + RootEnvelope.Size;
        var bytes = tail.GetSpan(start);
        var envelope = bytes[header..];

        if (header != 0)
        {
            TransactionFormat.WriteHeader(bytes, sequence, effects);
        }

        TransactionFormat.WriteEnvelopeOfWrite(envelope, RootLength(change, arena));
        RootEnvelope.Write(envelope[TransactionFormat.WriteEnvelopeSize..], (uint)change.Table.EntityType.Id, sequence, change.Member);
        tail.Advance(start);

        BlockExport.Write(tail, arena, layout, change.Root);
    }

    // A Delete of the key of the change, after the header of the transaction when header is its size. The
    // key goes into the spans the tail gives (Entities.PrimaryKeys.WriteKey): the text of a String key may
    // be longer than a block.
    private static void WriteDelete(DeliveryTail tail, ulong sequence, long effects, int header, in PreparedChange change)
    {
        var start = header + TransactionFormat.DeleteEnvelopeSize;
        var bytes = tail.GetSpan(start);
        var effect = bytes[header..];

        if (header != 0)
        {
            TransactionFormat.WriteHeader(bytes, sequence, effects);
        }

        TransactionFormat.WriteEnvelopeOfDelete(effect, (uint)change.Table.EntityType.Id);
        tail.Advance(start);
        change.Table.Keys.WriteKey(change.Key, tail);
    }

    // The bytes of the root of the change with its envelope.
    private static long RootLength(in PreparedChange change, Arena arena) =>
        RootEnvelope.Size + BlockExport.Length(arena, change.Table.Layout, change.Root);

    // A transaction of a write that changed nothing.
    public static void Unchanged(DeliveryTail tail, ulong sequence)
    {
        const int Length = TransactionFormat.HeaderSize + TransactionFormat.UnchangedSize;

        tail.Begin(sequence, TransactionFormat.UnchangedSize);

        var bytes = tail.GetSpan(Length);

        TransactionFormat.WriteHeader(bytes, sequence, TransactionFormat.UnchangedSize);
        TransactionFormat.WriteUnchanged(bytes[TransactionFormat.HeaderSize..]);
        tail.Advance(Length);
    }
}
