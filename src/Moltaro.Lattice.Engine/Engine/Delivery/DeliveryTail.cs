using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Replication.Transactions;

namespace Moltaro.Lattice.Engine.Delivery;

// The committed transactions a master holds for its children (brief of Epic 1, sprint S11; the planner's
// decision of 17 November 2026 after the study, points 2 and 5; milestone contract, "Replication
// transport and initial loading"): one shared tail, appended to in the order of the sequences, with one
// cursor for each child, from the registration of its capture until its connection ends.
//
// Blocks. The bytes of the transactions (Replication.Transactions.TransactionFormat) are written one
// after another into blocks of BlockSize bytes (DeliveryBlock), the form of the replication buffer of
// Valkey that the study of S11 read: a transaction is written into the current block and continued in
// the next one when it does not fit, except that one a block holds whole begins a block of its own when
// the rest of the current one cannot hold it, so that it travels in as few frames as it would alone. A
// block is in the large object heap, so the bytes waiting for an Applied are never copied by a
// collection of the young generation, and a write allocates nothing for the tail while a released block
// is there to be refilled: the tail keeps one spare, under the lock of the writes; any other block it
// releases is let go of. A tail of one array for each transaction allocated two objects a write and had
// its transactions copied by the collector (the measurements of 17 and 18 November 2026).
//
// Appending. A write that is published while a cursor is registered (Retains) is encoded in the hold of
// the lock of the writes that publishes it, before its stores (Begin, then the bytes through this
// IBufferWriter), and appended after them (Commit), so the tail has the order of the positions with no
// other mechanism, and a capture registered in a hold of that lock (Subscribe) has every transaction
// after its start in the tail, none before. Until Commit nothing of the transaction is visible: the end
// of the current block and the link to a block taken for it are stored at Commit; a write that fails
// before leaves bytes beyond the end, which the next transaction writes over. With no cursor registered
// nothing is encoded or retained.
//
// Releasing. Every transaction up to the lowest position the cursors have acknowledged (Acknowledge) —
// where a capture that has not ended counts at its start — is released: its bytes leave PendingBytes,
// which is what was appended less what was released, so it counts the bytes of the transactions some
// child has not applied, 0 once every child has applied everything and 0 with no child. The tail finds
// them by whole blocks, each of which records the last transaction that ends in it, and by their headers
// only inside the block that holds the lowest position, so that a release costs a step for each block it
// passes and at most a block's worth of headers, however far a child lagged. A block is let go of once every
// transaction with bytes in it is released and the sender of every cursor has moved past it
// (DeliveryCursor.Block): a sender reads a block only while its cursor names it, and moves on only
// forward, to the block the one it names links. So a block let go of is one no sender reads or will
// read, and it is emptied then, its link to the next block included, so that it keeps no later block
// reachable; that is why the spare can be refilled. A child that has gone takes its cursor with it
// (Unsubscribe). Until S33 the tail has no bound: a child that applies nothing makes it grow, and a write
// of the master is never refused or delayed because of it. The tail is managed memory outside the budget
// of the instance.
//
// Who calls what. Every member but Wake and WaitForMoreAsync is called under the lock of the writes,
// which protects the state of the tail; the senders of the children read the blocks without it
// (DeliveryReader), and wait for more outside every lock (WaitForMoreAsync), woken by the writer after it
// has released the lock (Wake).
internal sealed class DeliveryTail : IBufferWriter<byte>
{
    // The bytes of a block: above the 85,000 bytes from which an array is in the large object heap, and
    // more than the largest span a transaction asks for at once, the payload of one block of the arena
    // with the envelopes before it. Chosen by measurement against 256 KiB (following-children and
    // readers-beside-child, 18 November 2026): with blocks of 256 KiB a master with children allocated
    // 184 to 285 bytes a write for 16 callers against 171 to 205 with 1 MiB, and beside 16 readers its
    // collections of gen1 rose to 135 to 177 a window against 0 to 3, as the tail took blocks it had no
    // spare for more often.
    public const int BlockSize = 1024 * 1024;

    // The cursors of the children registered, few: one for each child connected or loading.
    private readonly List<DeliveryCursor> _cursors = [];

    // The blocks kept, from the oldest to the current one, which the next transaction is written into;
    // null while no cursor is registered.
    private DeliveryBlock? _first;

    private DeliveryBlock? _current;

    // A released block, emptied, that the next block taken refills.
    private DeliveryBlock? _spare;

    // Where the first transaction not released yet begins: a block of the tail and a place in it.
    private DeliveryBlock? _released;

    private int _releasedAt;

    // The sequence of the last transaction appended.
    private ulong _lastSequence;

    private long _appendedBytes;

    private long _releasedBytes;

    private int _captures;

    // The transaction between Begin and Commit: its sequence and length, the block its bytes are written
    // into and where, the end of its bytes in the current block once it continued in a block taken for it,
    // and the first such block.
    private ulong _sequence;

    private long _length;

    private DeliveryBlock? _writing;

    private int _writingAt;

    private int _currentEnd;

    private DeliveryBlock? _taken;

    // Completed at the next Wake; a waiting sender makes it, so that an append nobody waits for
    // allocates nothing.
    private TaskCompletionSource? _signal;

    // Whether a child or a capture is registered, so that a write that is published is appended.
    public bool Retains => _cursors.Count > 0;

    // The captures registered and not ended (EngineCounters.ActiveCaptures).
    public int ActiveCaptures => _captures;

    // The bytes of the transactions appended and not released (EngineCounters.PendingDeliveryBytes).
    public long PendingBytes => _appendedBytes - _releasedBytes;

    // Begins the transaction of the sequence, whose effects take the bytes given: its bytes are written
    // through GetSpan and Advance, its header first, and appended by Commit.
    public void Begin(ulong sequence, long effectsLength)
    {
        var current = _current!;

        _sequence = sequence;
        _length = TransactionFormat.HeaderSize + effectsLength;
        _writing = current;
        _writingAt = current.Used;
        _taken = null;

        if (_length <= BlockSize && _length > BlockSize - _writingAt && _writingAt > 0)
        {
            Continue();
        }
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_writing!.Bytes.Length - _writingAt < Math.Max(sizeHint, 1))
        {
            Continue();
        }

        return _writing!.Bytes.AsMemory(_writingAt);
    }

    public void Advance(int count) => _writingAt += count;

    // Appends the transaction begun, in the hold that published it.
    public void Commit()
    {
        var current = _current!;

        if (_taken is null)
        {
            current.Publish(_writingAt);
        }
        else
        {
            // The blocks taken are not reachable yet: their ends and links were stored before the link
            // from the current block, which a sender follows only after it has read the current block's
            // end.
            _writing!.Publish(_writingAt);

            if (_currentEnd > current.Used)
            {
                current.Publish(_currentEnd);
            }

            current.Link(_taken);
            _current = _writing;
        }

        _appendedBytes += _length;
        _lastSequence = _sequence;

        // The block the transaction ends in (see DeliveryBlock), for a release by whole blocks.
        _writing!.EndSequence = _sequence;
        _writing.EndOffset = _writingAt;
        _writing.EndAppended = _appendedBytes;
        _writing = null;

        // The blocks taken are linked from the current block now: the tail keeps them through its list
        // alone, so that one let go of is not kept reachable from here.
        _taken = null;
    }

    // Registers the cursor of a child whose capture starts at the published position given: every
    // transaction published from this hold on is appended for it, from where the current block ends.
    public DeliveryCursor Subscribe(ulong startPosition)
    {
        if (_current is null)
        {
            _current = Take();
            _first = _current;
            _released = _current;
            _releasedAt = 0;
        }

        var cursor = new DeliveryCursor(_current, _current.Used, startPosition);

        _cursors.Add(cursor);
        _captures++;

        return cursor;
    }

    // The capture of the cursor has copied its last root; it holds the tail from its start until its
    // child acknowledges.
    public void EndCapture(DeliveryCursor cursor)
    {
        if (cursor.Capturing)
        {
            cursor.Capturing = false;
            _captures--;
        }
    }

    // The child of the cursor has applied the stream up to the sequence: what no child needs any more is
    // released. False for a sequence before what it acknowledged already or after what it was sent, which
    // no child can have applied; nothing changes then.
    public bool Acknowledge(DeliveryCursor cursor, ulong applied)
    {
        if (applied < cursor.Acknowledged || applied > cursor.Sent)
        {
            return false;
        }

        cursor.Acknowledged = applied;
        Release();

        return true;
    }

    // The connection of the cursor has ended, its sender with it: its capture, if it was copying, ends,
    // and the tail is no longer held for it.
    public void Unsubscribe(DeliveryCursor cursor)
    {
        EndCapture(cursor);

        if (_cursors.Remove(cursor))
        {
            Release();
        }
    }

    // Outside every lock, after a hold that appended: the senders that wait look again.
    public void Wake() => Interlocked.Exchange(ref _signal, null)?.TrySetResult();

    // Outside every lock: completes once bytes are there to read after the place given, in the block or
    // in a block after it, or a reply of the sender's connection waits (RecordedReplies, whose refusals
    // wake the senders as an append does). The token ends the wait, with OperationCanceledException; the
    // close of the master is in it.
    public async ValueTask WaitForMoreAsync(DeliveryBlock block, int offset, RecordedReplies replies, CancellationToken cancellationToken)
    {
        while (true)
        {
            // The signal is taken before the block and the replies are read: an append or a reply after
            // the read completes this signal, one before it is seen.
            var signal = Volatile.Read(ref _signal);

            if (signal is null)
            {
                var made = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                signal = Interlocked.CompareExchange(ref _signal, made, null) ?? made;
            }

            if (offset < block.Used || block.Next is not null || replies.HasReplies)
            {
                return;
            }

            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The transaction continues in a block taken for it (see the class).
    private void Continue()
    {
        var next = Take();

        if (ReferenceEquals(_writing, _current))
        {
            _currentEnd = _writingAt;
            _taken = next;
        }
        else
        {
            _writing!.Publish(_writingAt);
            _writing.Link(next);
        }

        _writing = next;
        _writingAt = 0;
    }

    // A block to write into: the spare, emptied when it was let go of, or a new one.
    private DeliveryBlock Take()
    {
        var block = _spare ?? new DeliveryBlock(BlockSize);

        _spare = null;

        return block;
    }

    // Releases every transaction up to the lowest position acknowledged and lets go of every block it
    // empties that no sender reads (see the class), or of everything when no cursor is registered.
    private void Release()
    {
        if (_current is null)
        {
            return;
        }

        if (_cursors.Count == 0)
        {
            // No sender is left: every connection with a cursor ended before its cursor went. With no
            // child and no capture the master retains nothing, its blocks included; the next cursor takes
            // a block anew (Subscribe). Each block let go of is emptied, as one let go of below is, so
            // that a block something else still holds keeps no later one reachable.
            for (var block = _first; block is not null;)
            {
                var next = block.Next;

                block.Reset();
                block = next;
            }

            _releasedBytes = _appendedBytes;
            _first = null;
            _current = null;
            _released = null;
            _spare = null;
            _taken = null;

            return;
        }

        var lowest = ulong.MaxValue;

        foreach (var cursor in _cursors)
        {
            lowest = Math.Min(lowest, cursor.Acknowledged);
        }

        if (lowest >= _lastSequence)
        {
            // Every child has applied everything: nothing to read.
            _released = _current;
            _releasedAt = _current.Used;
            _releasedBytes = _appendedBytes;
        }
        else
        {
            PassWholeBlocks(lowest);

            while (TryUnreleased(out var sequence, out var length) && sequence <= lowest)
            {
                _releasedBytes += length;
                Pass(length);
            }
        }

        while (!ReferenceEquals(_first, _released) && !HoldsASender(_first!))
        {
            var passed = _first!;

            _first = passed.Next;

            // No sender reads it, none can reach it again, and emptied it links no later block.
            passed.Reset();
            _spare ??= passed;
        }
    }

    // Moves the start of what is not released, block by block, to the end of the last transaction that
    // every child has applied and that ends in a block before the one that holds the lowest position, so
    // that only the headers inside that block are read (Release): a block in which a transaction ends
    // records the last of them at Commit, and the transactions of a block are in the order of their
    // sequences. A block in which none ends lies inside one transaction, and the search goes on past it.
    private void PassWholeBlocks(ulong lowest)
    {
        var block = _released!;

        while (true)
        {
            if (block.EndSequence > lowest)
            {
                return;
            }

            if (block.EndSequence != 0 && block.EndAppended > _releasedBytes)
            {
                _released = block;
                _releasedAt = block.EndOffset;
                _releasedBytes = block.EndAppended;
            }

            if (ReferenceEquals(block, _current))
            {
                return;
            }

            block = block.Next!;
        }
    }

    // The sequence and the length of the first transaction not released, from its header, which is in one
    // block (see the class); false when every transaction appended is released.
    private bool TryUnreleased(out ulong sequence, out long length)
    {
        if (_releasedAt == _released!.Used)
        {
            // The end of a block that is not the current one is final.
            if (ReferenceEquals(_released, _current))
            {
                sequence = 0;
                length = 0;

                return false;
            }

            _released = _released.Next!;
            _releasedAt = 0;
        }

        var header = _released.Bytes.AsSpan(_releasedAt, TransactionFormat.HeaderSize);

        sequence = BinaryPrimitives.ReadUInt64LittleEndian(header);
        length = TransactionFormat.HeaderSize + (long)BinaryPrimitives.ReadUInt64LittleEndian(header[sizeof(ulong)..]);

        return true;
    }

    // Moves the start of what is not released past the bytes given, from block to block.
    private void Pass(long length)
    {
        while (true)
        {
            var count = (int)Math.Min(length, _released!.Used - _releasedAt);

            _releasedAt += count;
            length -= count;

            if (length == 0)
            {
                return;
            }

            _released = _released.Next!;
            _releasedAt = 0;
        }
    }

    private bool HoldsASender(DeliveryBlock block)
    {
        foreach (var cursor in _cursors)
        {
            if (ReferenceEquals(cursor.Block, block))
            {
                return true;
            }
        }

        return false;
    }
}
