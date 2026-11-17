using System.Buffers.Binary;

namespace Moltaro.Lattice.Engine.Delivery;

// How the sender of one child reads the tail (DeliveryTail), outside every lock: from where its stream
// begins, block after block, only the bytes a block has published as complete (DeliveryBlock). Before it
// reads a block it names it in its cursor, so that the block is not released while it reads it; it moves
// to the next block only once it has read this one to its end, and the end of a block is published
// before the link to the next.
internal sealed class DeliveryReader
{
    private readonly DeliveryCursor _cursor;

    private DeliveryBlock _block;

    private int _offset;

    public DeliveryReader(DeliveryCursor cursor)
    {
        _cursor = cursor;
        _block = cursor.Block;
        _offset = cursor.StartOffset;
    }

    // Where the reader stands, for a wait for more (DeliveryTail.WaitForMoreAsync).
    public DeliveryBlock Block => _block;

    public int Offset => _offset;

    // Whether bytes are there to read, in this block or, once it is read to its end, in the next.
    public bool HasBytes()
    {
        while (true)
        {
            if (_offset < _block.Used)
            {
                return true;
            }

            var next = _block.Next;

            if (next is null)
            {
                return false;
            }

            // The end of this block was published before the link: read it again.
            if (_offset < _block.Used)
            {
                return true;
            }

            _cursor.Block = next;
            _block = next;
            _offset = 0;
        }
    }

    // The sequence and the length of the transaction that starts here, its header and its effects; its
    // header is in one block (DeliveryTail). HasBytes was true.
    public (ulong Sequence, long Length) Transaction()
    {
        var header = _block.Bytes.AsSpan(_offset);
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(header);
        var effects = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[sizeof(ulong)..]);

        return (sequence, Replication.Transactions.TransactionFormat.HeaderSize + effects);
    }

    // The next bytes, at most the count given, all of one block. HasBytes was true.
    public ReadOnlySpan<byte> Take(long most)
    {
        var count = (int)Math.Min(most, _block.Used - _offset);
        var bytes = _block.Bytes.AsSpan(_offset, count);

        _offset += count;

        return bytes;
    }
}
