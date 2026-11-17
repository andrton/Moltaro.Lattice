using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Delivery;

// One committed transaction of a master in the tail it delivers to its children (DeliveryTail), as the
// bytes that travel (Replication.Transactions.TransactionFormat), encoded once, in the hold of the lock of
// the writes that publishes it, and sent to every child from there (the planner's decision of
// 17 November 2026 after the study of S11, points 5 and 6). A transaction is one write until S17 brings
// the batch: an insert, a replace or an upsert carries the root it publishes, a delete the key, a write
// that changed nothing only its position.
//
// The transactions of the tail are linked in the order of their sequences. The link to the next one is
// stored with release semantics after that one is complete and loaded with acquire semantics, so the
// sender of a child, which follows the links without a lock (Children.ChildConnection), sees every byte
// of a transaction it reaches. The bytes are let go of when every child has applied the transaction
// (Release); no sender reads them any more by then, since a child applies only what was sent to it.
internal sealed class DeliveredTransaction
{
    private DeliveredTransaction? _next;

    private DeliveredTransaction(ulong sequence, byte[]? bytes)
    {
        Sequence = sequence;
        Bytes = bytes;
        Length = bytes?.Length ?? 0;
    }

    public ulong Sequence { get; }

    // The bytes of the transaction; null for the start of a tail, and once released.
    public byte[]? Bytes { get; private set; }

    // The bytes it holds or held, for PendingDeliveryBytes.
    public long Length { get; }

    // The transaction after this one, once it is complete.
    public DeliveredTransaction? Next => Volatile.Read(ref _next);

    // A node that holds no transaction and stands for the position given: the start of a tail.
    public static DeliveredTransaction Start(ulong sequence) => new(sequence, bytes: null);

    // The bytes of a transaction whose one effect carries the root of the entity type of the table.
    // The root is the one the write publishes, complete in its prepared blocks; nothing changes it while
    // the lock of the writes is held. Null when the transaction is longer than an array holds
    // (Array.MaxLength): the tail cannot hold it, and the caller drops what it delivers (DeliveryTail.Drop).
    public static DeliveredTransaction? OfWrite(ulong sequence, EntityTable table, Arena arena, ArenaAddress root)
    {
        var layout = table.Layout!;
        var rootLength = TransactionFormat.RootEnvelopeSize + BlockExport.Length(arena, layout, root);
        var effects = TransactionFormat.WriteEnvelopeSize + rootLength;

        if (TransactionFormat.HeaderSize + effects > Array.MaxLength)
        {
            return null;
        }

        var bytes = Allocate(sequence, effects);
        var output = new ExactBufferWriter(bytes, TransactionFormat.HeaderSize);
        var envelope = output.GetSpan(TransactionFormat.WriteEnvelopeSize + TransactionFormat.RootEnvelopeSize);

        envelope[0] = (byte)EffectKindEnum.Write;
        BinaryPrimitives.WriteUInt64LittleEndian(envelope[1..], (ulong)rootLength);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope[TransactionFormat.WriteEnvelopeSize..], (uint)table.EntityType.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(envelope[(TransactionFormat.WriteEnvelopeSize + sizeof(uint))..], sequence);
        output.Advance(TransactionFormat.WriteEnvelopeSize + TransactionFormat.RootEnvelopeSize);

        BlockExport.Write(output, arena, layout, root);

        return new DeliveredTransaction(sequence, bytes);
    }

    // The bytes of a transaction whose one effect deletes the key of the entity type of the table.
    public static DeliveredTransaction OfDelete(ulong sequence, EntityTable table, in EntityKey key)
    {
        var bytes = Allocate(sequence, TransactionFormat.DeleteEnvelopeSize + table.KeyLength(key));
        var effect = bytes.AsSpan(TransactionFormat.HeaderSize);

        effect[0] = (byte)EffectKindEnum.Delete;
        BinaryPrimitives.WriteUInt32LittleEndian(effect[1..], (uint)table.EntityType.Id);
        table.WriteKey(key, effect[TransactionFormat.DeleteEnvelopeSize..]);

        return new DeliveredTransaction(sequence, bytes);
    }

    // The bytes of a transaction of a write that changed nothing.
    public static DeliveredTransaction OfUnchanged(ulong sequence)
    {
        var bytes = Allocate(sequence, TransactionFormat.UnchangedSize);

        bytes[TransactionFormat.HeaderSize] = (byte)EffectKindEnum.Unchanged;

        return new DeliveredTransaction(sequence, bytes);
    }

    // Under the lock of the writes, by the tail: the transaction comes after this one.
    public void Link(DeliveredTransaction next) => Volatile.Write(ref _next, next);

    // Under the lock of the writes, by the tail, once every child has applied the transaction.
    public void Release() => Bytes = null;

    // An array of the exact length of a transaction with its header written, uninitialized beyond it: the
    // effects are written over every byte of it.
    private static byte[] Allocate(ulong sequence, long effects)
    {
        var bytes = GC.AllocateUninitializedArray<byte>((int)(TransactionFormat.HeaderSize + effects));

        TransactionFormat.WriteHeader(bytes, sequence, effects);

        return bytes;
    }
}
