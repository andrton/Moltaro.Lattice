using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Delivery;

// Writes one committed transaction of a master into the tail it delivers to its children (DeliveryTail),
// as the bytes that travel (Replication.Transactions.TransactionFormat), once, in the hold of the lock of
// the writes that publishes it (the planner's decision of 17 November 2026 after the study of S11, points
// 5 and 6). A transaction is one write until S17 brings the batch: an insert, a replace or an upsert
// carries the root it publishes, a delete the key, a write that changed nothing only its position. The
// header and the envelope of the effect are asked for in one span, so that they are in one block. The
// caller appends it after the stores of the write (DeliveryTail.Commit).
internal static class TransactionEncoder
{
    // A transaction whose one effect carries the root of the entity type of the table. The root is the
    // one the write publishes, complete in its prepared blocks; nothing changes it while the lock of the
    // writes is held.
    public static void Write(DeliveryTail tail, ulong sequence, EntityTable table, Arena arena, ArenaAddress root)
    {
        var layout = table.Layout!;
        var rootLength = TransactionFormat.RootEnvelopeSize + BlockExport.Length(arena, layout, root);
        var effects = TransactionFormat.WriteEnvelopeSize + rootLength;
        const int Start = TransactionFormat.HeaderSize + TransactionFormat.WriteEnvelopeSize + TransactionFormat.RootEnvelopeSize;

        tail.Begin(sequence, effects);

        var start = tail.GetSpan(Start);
        var envelope = start[TransactionFormat.HeaderSize..];

        TransactionFormat.WriteHeader(start, sequence, effects);
        envelope[0] = (byte)EffectKindEnum.Write;
        BinaryPrimitives.WriteUInt64LittleEndian(envelope[1..], (ulong)rootLength);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope[TransactionFormat.WriteEnvelopeSize..], (uint)table.EntityType.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(envelope[(TransactionFormat.WriteEnvelopeSize + sizeof(uint))..], sequence);
        tail.Advance(Start);

        BlockExport.Write(tail, arena, layout, root);
    }

    // A transaction whose one effect deletes the key of the entity type of the table.
    public static void Delete(DeliveryTail tail, ulong sequence, EntityTable table, in EntityKey key)
    {
        var effects = TransactionFormat.DeleteEnvelopeSize + table.KeyLength(key);
        var length = TransactionFormat.HeaderSize + effects;

        tail.Begin(sequence, effects);

        var bytes = tail.GetSpan(length);
        var effect = bytes[TransactionFormat.HeaderSize..];

        TransactionFormat.WriteHeader(bytes, sequence, effects);
        effect[0] = (byte)EffectKindEnum.Delete;
        BinaryPrimitives.WriteUInt32LittleEndian(effect[1..], (uint)table.EntityType.Id);
        table.WriteKey(key, effect[TransactionFormat.DeleteEnvelopeSize..]);
        tail.Advance(length);
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
