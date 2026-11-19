using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Delivery;

// Writes one committed transaction of a master into the tail it delivers to its children (DeliveryTail),
// as the bytes that travel (Replication.Transactions.TransactionFormat), once, in the hold of the lock of
// the writes that publishes it (docs/design/milestone-1-engine-contract.md, "Replication transport and
// initial loading"). A transaction is one write, since the write batch is not built yet: an insert, a
// replace or an upsert carries the root it publishes, a delete the key, a write that changed nothing only
// its position. The header and the envelope of the effect are asked for in one span, so that they are in
// one block; no span asked for is larger than a block: the root payload is at most a block of the arena,
// and the values out of line and the text of a key go into the spans as the tail gives them. The caller
// appends it after the stores of the write (DeliveryTail.Commit).
internal static class TransactionEncoder
{
    // A transaction whose one effect carries the root of the entity type of the table. The root is the
    // one the write publishes, complete in its prepared blocks; nothing changes it while the lock of the
    // writes is held.
    public static void Write(DeliveryTail tail, ulong sequence, EntityTable table, Arena arena, ArenaAddress root)
    {
        var layout = table.Layout!;
        var rootLength = RootEnvelope.Size + BlockExport.Length(arena, layout, root);
        var effects = TransactionFormat.WriteEnvelopeSize + rootLength;
        const int Start = TransactionFormat.HeaderSize + TransactionFormat.WriteEnvelopeSize + RootEnvelope.Size;

        tail.Begin(sequence, effects);

        var start = tail.GetSpan(Start);
        var envelope = start[TransactionFormat.HeaderSize..];

        TransactionFormat.WriteHeader(start, sequence, effects);
        envelope[0] = (byte)EffectKindEnum.Write;
        BinaryPrimitives.WriteUInt64LittleEndian(envelope[1..], (ulong)rootLength);
        RootEnvelope.Write(envelope[TransactionFormat.WriteEnvelopeSize..], (uint)table.EntityType.Id, sequence);
        tail.Advance(Start);

        BlockExport.Write(tail, arena, layout, root);
    }

    // A transaction whose one effect deletes the key of the entity type of the table. The key goes into the
    // spans the tail gives (Entities.PrimaryKeys.WriteKey): the text of a String key may be longer than a block.
    public static void Delete(DeliveryTail tail, ulong sequence, EntityTable table, in EntityKey key)
    {
        var effects = TransactionFormat.DeleteEnvelopeSize + table.Keys.KeyLength(key);
        const int Start = TransactionFormat.HeaderSize + TransactionFormat.DeleteEnvelopeSize;

        tail.Begin(sequence, effects);

        var start = tail.GetSpan(Start);
        var effect = start[TransactionFormat.HeaderSize..];

        TransactionFormat.WriteHeader(start, sequence, effects);
        effect[0] = (byte)EffectKindEnum.Delete;
        BinaryPrimitives.WriteUInt32LittleEndian(effect[1..], (uint)table.EntityType.Id);
        tail.Advance(Start);
        table.Keys.WriteKey(key, tail);
    }

    // A transaction of a write that changed nothing.
    public static void Unchanged(DeliveryTail tail, ulong sequence)
    {
        const int Length = TransactionFormat.HeaderSize + TransactionFormat.UnchangedSize;

        tail.Begin(sequence, TransactionFormat.UnchangedSize);

        var bytes = tail.GetSpan(Length);

        TransactionFormat.WriteHeader(bytes, sequence, TransactionFormat.UnchangedSize);
        bytes[TransactionFormat.HeaderSize] = (byte)EffectKindEnum.Unchanged;
        tail.Advance(Length);
    }
}
