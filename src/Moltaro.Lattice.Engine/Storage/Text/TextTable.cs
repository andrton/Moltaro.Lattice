using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Text;

// The text entries of one database (brief of Epic 1, sprint S13; decision on strings, Q04.2 to Q04.4;
// the planner's decision after the study of S13). Equal text of every field with Dictionary storage, of
// every entity type, is stored once: as one entry, immutable memory of the arena that holds the exact
// stored bytes and nothing else, one block up to 4 KiB and a directory with chunks above, by the
// placement of a Plain value out of line, every block of kind SharedText and its header naming the slot
// of the entry (Values.ValueBlocks.TryStoreText). A block refers to an entry by a descriptor of kind Text
// with the length of the text and the relative address of the entry. An empty text stays inline with
// length 0 and has no entry.
//
// Who uses it. Writes only: a write of a Dictionary text finds or adds its entry here (TryAcquire), and
// the free of a block releases the references it holds (Release), on a master and on a child, which
// builds its own entries from the bytes it receives with its own seed. A read reaches a text through the
// address its root holds, inside the protection of reads, and never touches this table: no lock and no
// gate of its own, nothing of it on a line a read reads.
//
// One count for each entry, by one rule (point 4): a block holds one reference for each Text descriptor
// it contains, from the moment it is written (RootWriter, Replication.Load.BlockImport, through
// TryAcquire) until it is freed (Storage.RootStorage.FreeValue, through Release). The rule is worded for
// any block: a root now, an owned value or a collection with S18 and S19, an index entry later. A
// prepared block holds its references as a live one does, so the descriptors of a prepared root are the
// tally of what its write took, and a write that is withdrawn, cancelled, refused or ended by a fault
// drops them where its blocks are freed. A retired block holds them until the drain frees it, which is
// after the last read that could reach it has left.
//
// No retirement of an entry on its own, and no generation (point 5). An entry whose count reaches zero
// leaves the bucket table and is freed in the same call: no block refers to it any more, and a read
// reaches an entry only through a block it reached inside the protection, which is not freed while the
// read is inside. So an entry is freed only after its last reference and the last protected reader that
// could reach it are gone, and a reference never resolves to other text. A later write of the same text
// makes a new entry; an entry that left the table is never found again.
//
// Where counts change (point 6, the P1 of the sprint). Every member that changes a count runs on the
// thread that holds the lock of the writes (Engine.Writes.WriteCoordinator), on a master and on a child:
// the preparation of a write, the placement of a received root, the free of what a write prepared and
// does not publish, and the drain of retired memory — under that lock also when the close frees what is
// retired last (Engine.EngineDatabase). The table takes no lock; in a Debug build every change of a
// count asserts that the thread holds the lock the database gave the arena, which is how a test shows it.
//
// The parts. Slots in a managed array that grows by doubling up to the longest array of .NET, and refuses
// an entry beyond it (TryGrowSlots; TextSlot: the address, the references, the hash, the length, the
// state or the link of the free list), and the bucket table of the primary index from the hash to the slot (Hashing.IndexTable), the same type and not a second one. The hash is the
// engine's (Hashing.Marvin) over the stored bytes, with the seed of the instance. An entry is keyed by its
// exact bytes always: North and north are two entries, and the comparison a field declares applies only
// where text is compared. No read touches the slots or the buckets, so a full bucket table is replaced by
// a grown one at once, without retirement, and the old one is let go of.
internal sealed class TextTable
{
    // The first bucket table: two main buckets.
    private const int FirstBucketBits = 1;

    // The bytes of the scratch on the stack in which a write encodes its text: every text of an
    // ordinary field fits it, and a longer one is encoded in pieces of it.
    private const int ScratchBytes = 512;

    private readonly Arena _arena;

    private readonly ulong _seed;

    // The lock of the writes of the database, for the assertion of Debug builds; null for an arena of a
    // test of the engine that has no database around it.
    private readonly Lock? _writes;

    private IndexTable _buckets = new(FirstBucketBits, version: 0);

    private TextSlot[] _slots = [];

    // The slots below this were given out at least once.
    private int _nextUnusedSlot;

    // The first free slot plus one; 0 when none is free.
    private uint _freeSlots;

    // The table of the arena given, with the seed of its hashes and the lock its database changes it
    // under.
    public TextTable(Arena arena, ulong seed, Lock? writes)
    {
        _arena = arena;
        _seed = seed;
        _writes = writes;
    }

    // The entries the table holds: the texts some block that is not freed refers to.
    public int Count { get; private set; }

    // The managed bytes of the table: the array of its slots and the bucket table.
    public long Bytes => ((long)_slots.Length * Unsafe.SizeOf<TextSlot>()) + _buckets.Bytes;

    // Finds the entry of the text of a write, or adds one, takes one reference to it and writes the Text
    // descriptor that names it into place; for RootWriter, under the lock of the writes. The text is
    // well-formed and takes length stored bytes, at least one, in the encoding of the database. True with
    // the bytes of the slots of a new entry in newBytes, 0 when the entry was there; false when the arena
    // cannot hold a new entry, with nothing taken and place as it was. A new entry is prepared when
    // prepared is true, and published with the first published block that refers to it (Publish); a
    // write of live blocks publishes an entry it finds prepared. An OutOfMemoryException of the native
    // allocator or of the managed heap passes with nothing taken.
    public bool TryAcquire(ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding, int length, bool prepared, Span<byte> place, out long newBytes)
    {
        Span<byte> scratch = stackalloc byte[ScratchBytes];
        var pieces = TextPieces.OfText(text, encoding, length, scratch);

        return TryAcquire(ref pieces, length, prepared, place, out newBytes);
    }

    // The same, for the bytes of a Dictionary text a node received, as its sender stored them, length
    // of them, at least one; for Replication.Load.BlockImport, under the lock of the writes.
    public bool TryAcquire(ReadOnlySequence<byte> bytes, bool prepared, Span<byte> place, out long newBytes)
    {
        var pieces = TextPieces.OfReceived(bytes);

        return TryAcquire(ref pieces, (int)bytes.Length, prepared, place, out newBytes);
    }

    // Releases the one reference the Text descriptor holds, for the free of the block that holds it
    // (Storage.RootStorage.FreeValue), under the lock of the writes. An entry whose count reaches zero
    // leaves the bucket table and its blocks are freed, prepared or live, in this call (see the class).
    public void Release(in ValueDescriptor descriptor)
    {
        AssertTheLockIsHeld();

        var address = ValueBlocks.AddressOf(descriptor);
        var number = SlotOf(address);
        ref var slot = ref _slots[number];

        Debug.Assert(slot.Address == address && slot.Length == descriptor.Length && slot.References > 0, "A Text descriptor names the entry of its slot.");

        if (--slot.References > 0)
        {
            return;
        }

        _buckets.Remove(slot.Hash, number);
        ValueBlocks.FreeText(_arena, address, slot.Length);

        slot = new TextSlot { State = _freeSlots };
        _freeSlots = number + 1;
        Count--;
    }

    // Publishes the entry the Text descriptor names when it is still prepared, for the publication of a
    // block that holds the descriptor (Storage.RootStorage.Publish), under the lock of the writes. It
    // allocates nothing and changes no count.
    public void Publish(in ValueDescriptor descriptor)
    {
        var address = ValueBlocks.AddressOf(descriptor);
        ref var slot = ref _slots[SlotOf(address)];

        if (slot.State == TextSlot.Prepared)
        {
            ValueBlocks.PublishText(_arena, address, slot.Length);
            slot.State = TextSlot.Published;
        }
    }

    // For the tests of the engine (EngineDatabase.TextReferences): each entry with its exact stored bytes
    // and its references, under the lock of the writes.
    public List<(byte[] Text, long References)> References()
    {
        var references = new List<(byte[] Text, long References)>(Count);

        for (var number = 0; number < _nextUnusedSlot; number++)
        {
            ref var slot = ref _slots[number];

            if (slot.Address.IsNull)
            {
                continue;
            }

            var descriptor = DescriptorOf(slot);
            var bytes = new byte[slot.Length];

            ValueBlocks.CopyTo(_arena, descriptor, default, bytes);
            references.Add((bytes, slot.References));
        }

        return references;
    }

    private static ValueDescriptor DescriptorOf(in TextSlot slot) =>
        new() { Kind = DescriptorKindEnum.Text, Length = slot.Length, Location = slot.Address.Value };

    // The search and the addition of TryAcquire (see there).
    private bool TryAcquire(ref TextPieces pieces, int length, bool prepared, Span<byte> place, out long newBytes)
    {
        AssertTheLockIsHeld();

        var hash = HashOf(ref pieces);
        var probe = new IndexProbe(_buckets, hash);

        while (probe.Next(out var number))
        {
            ref var slot = ref _slots[number];

            if (slot.Length == (uint)length && HoldsTheBytes(slot, ref pieces))
            {
                slot.References++;

                if (!prepared && slot.State == TextSlot.Prepared)
                {
                    ValueBlocks.PublishText(_arena, slot.Address, slot.Length);
                    slot.State = TextSlot.Published;
                }

                ValueBlocks.Write(place, DescriptorOf(slot));
                newBytes = 0;

                return true;
            }
        }

        return TryAdd(ref pieces, hash, length, prepared, place, out newBytes);
    }

    // A new entry of the text: room in the bucket table and a slot first, then the blocks, so that a
    // refusal of the arena or a failure gives back only the slot. An array of slots that cannot grow any
    // more refuses the entry before anything is taken, as the arena refuses one it cannot hold.
    private bool TryAdd(ref TextPieces pieces, uint hash, int length, bool prepared, Span<byte> place, out long newBytes)
    {
        if (_buckets.IsFull(reserved: 0) && _buckets.BucketBits < IndexTable.MostBucketBits)
        {
            Grow();
        }

        _buckets.Reserve(hash, others: 0);

        if (!TryTakeSlot(out var number))
        {
            newBytes = 0;

            return false;
        }

        var source = pieces.Source();
        bool stored;

        try
        {
            stored = ValueBlocks.TryStoreText(_arena, new RootHandle(0, number), prepared, length, ref source, place);
        }
        catch
        {
            ReturnSlot(number);
            throw;
        }

        if (!stored)
        {
            ReturnSlot(number);
            newBytes = 0;

            return false;
        }

        _slots[number] = new TextSlot
        {
            Address = ValueBlocks.AddressOf(ValueBlocks.Read(place)),
            References = 1,
            Hash = hash,
            Length = (uint)length,
            State = prepared ? TextSlot.Prepared : TextSlot.Published,
        };

        _buckets.Add(hash, number);
        Count++;
        newBytes = ValueBlocks.SlotBytesOf(length);

        return true;
    }

    // The seeded hash of the stored bytes, in their order whatever the pieces.
    private uint HashOf(ref TextPieces pieces)
    {
        var marvin = new Marvin(_seed);

        pieces.Restart();

        for (var piece = pieces.Next(); !piece.IsEmpty; piece = pieces.Next())
        {
            marvin.Append(piece);
        }

        return marvin.Finish();
    }

    // Whether the entry of the slot holds exactly the bytes, which have its length: compared a piece at a
    // time with its one block, or with its chunks one after the other.
    private bool HoldsTheBytes(in TextSlot slot, ref TextPieces pieces)
    {
        var descriptor = DescriptorOf(slot);
        var chunked = ValueBlocks.LiesInChunks(descriptor);
        var entries = chunked ? ValueBlocks.Directory(_arena, descriptor) : default;
        var stored = chunked ? ValueBlocks.Chunk(_arena, entries, 0) : ValueBlocks.Contiguous(_arena, descriptor, default);
        var nextChunk = 1;

        pieces.Restart();

        for (var piece = pieces.Next(); !piece.IsEmpty; piece = pieces.Next())
        {
            while (!piece.IsEmpty)
            {
                if (stored.IsEmpty)
                {
                    stored = ValueBlocks.Chunk(_arena, entries, nextChunk++);
                }

                var count = Math.Min(piece.Length, stored.Length);

                if (!piece[..count].SequenceEqual(stored[..count]))
                {
                    return false;
                }

                piece = piece[count..];
                stored = stored[count..];
            }
        }

        return true;
    }

    // The slot of the entry whose first block is at the address, as its header names it.
    private uint SlotOf(ArenaAddress address) => _arena.Header(address).Root.Slot;

    // The length an array of slots of the length given grows to once every slot of it was given out:
    // twice the length, 4 for an empty array, and at most the longest array of .NET (Array.MaxLength).
    // False when no longer array can be had. A doubling past 2^30 slots does not fit an int, and the
    // array is never cut short for it: a table that cannot grow refuses the entry (TryAdd). A limit of
    // the structure, not an option (milestone contract, "Collections and strings").
    public static bool TryGrowSlots(int length, out int grown)
    {
        grown = (int)Math.Min(Math.Max(4L, 2L * length), Array.MaxLength);

        return grown > length;
    }

    // A free slot, the last freed first, or one never given out, after the array grew when every slot
    // was (TryGrowSlots); false, with nothing changed, when it cannot grow. The new array is allocated
    // before anything changes.
    private bool TryTakeSlot(out uint number)
    {
        if (_freeSlots != 0)
        {
            number = _freeSlots - 1;
            _freeSlots = _slots[number].State;
            _slots[number].State = 0;

            return true;
        }

        if (_nextUnusedSlot == _slots.Length)
        {
            if (!TryGrowSlots(_slots.Length, out var grown))
            {
                number = 0;

                return false;
            }

            Array.Resize(ref _slots, grown);
        }

        number = (uint)_nextUnusedSlot++;

        return true;
    }

    // Gives back a slot TakeSlot gave and no entry took.
    private void ReturnSlot(uint number)
    {
        _slots[number] = new TextSlot { State = _freeSlots };
        _freeSlots = number + 1;
    }

    // Replaces the bucket table by one of twice its main buckets, built from the words of the current
    // one; no read can hold the old one, so it is let go of at once.
    private void Grow()
    {
        var grown = new IndexTable(_buckets.BucketBits + 1, _buckets.Version + 1);

        _buckets.CopyInto(grown);
        _buckets = grown;
    }

    private void AssertTheLockIsHeld() =>
        Debug.Assert(_writes is null || _writes.IsHeldByCurrentThread, "A count of the text table changes only on the thread that holds the lock of the writes.");
}
