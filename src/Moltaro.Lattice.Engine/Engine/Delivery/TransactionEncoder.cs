using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Delivery;

// Writes one committed transaction of a master into the tail it delivers to its children (DeliveryTail),
// as the bytes that travel (Replication.Transactions.TransactionFormat), once, in the hold of the lock of
// the writes that publishes it (docs/design/milestone-1-engine-contract.md, "Replication transport and
// initial loading"). A transaction is one write: one effect for each change it publishes, in their order,
// the change of a single write or the changes of the members of a write batch; an insert, a replace or an
// upsert carries the root it publishes, with the number of its member, and a delete the key; a write that
// changed nothing carries only its position. The header and the envelope of the first effect are asked
// for in one span, so that they are in one block, and the envelope of each next effect in one span; no
// span asked for is larger than a block: the root payload is at most a block of the arena, and the values
// out of line and the text of a key go into the spans as the tail gives them. The caller appends it after
// the stores of the write (DeliveryTail.Commit).
internal static class TransactionEncoder
{
    // A transaction of the changes, which are not empty. The roots are those the write publishes, complete
    // in their prepared blocks; nothing changes them while the lock of the writes is held.
    public static void Write(DeliveryTail tail, ulong sequence, ReadOnlySpan<PreparedChange> changes, Arena arena)
    {
        var effects = 0L;

        foreach (ref readonly var change in changes)
        {
            effects += change.Kind == ChangeKindEnum.Delete
                ? TransactionFormat.DeleteEnvelopeSize + change.Table.Keys.KeyLength(change.Key)
                : TransactionFormat.WriteEnvelopeSize + RootLength(change, arena);
        }

        tail.Begin(sequence, effects);

        var header = TransactionFormat.HeaderSize;

        foreach (ref readonly var change in changes)
        {
            if (change.Kind == ChangeKindEnum.Delete)
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
