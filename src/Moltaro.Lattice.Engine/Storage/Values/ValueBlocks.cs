using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The blocks of one String or Binary value that does not lie inline: one block of kind Value, or a
// directory of kind ChunkDirectory with its chunks of kind Chunk, as RootLayout places them. What a
// root owns besides its root block is reached through these descriptors and nothing else, so storing,
// reading, adopting and freeing a value all start from its descriptor.
//
// The entry of a Dictionary text (Storage.Text.TextTable) is placed by the same rule, one block up to
// SingleBlockLimit bytes and a directory with chunks above, every block of it of kind
// SharedText, and a root names it by a descriptor of kind Text. A Text descriptor is read as a value
// in blocks is read, by its length, but its blocks belong to the entry and not to the root: Free,
// Publish, Retire and Adopt take a Block or a Chunked descriptor, as Storage.Roots.RootStorage decides by the
// kind, and the text table frees and publishes the blocks of an entry (FreeText, PublishText).
//
// Descriptors in a native block use ResidentDescriptor, even in a directory where the slots
// are already aligned. Read and Write below serve canonical scratch and transfer descriptors.
//
// A directory holds one descriptor for each chunk, in the order of the value: kind Block, the bytes
// of the chunk, the relative address of the chunk. Its payload is 16 bytes for each chunk and nothing
// else, so its length tells how many chunks the value has.
internal static class ValueBlocks
{
    // The bytes of a ValueDescriptor, in a root block and in a directory.
    public const int DescriptorSize = 16;

    // The most encoded bytes of a value stored inline, in the root block.
    public const int InlineLimit = 64;

    // The most encoded bytes of a value stored in one block of its own. A larger value is chunked.
    public const int SingleBlockLimit = 4 * 1024;

    // The payload bytes of one chunk: the largest payload of a block, Arena.MaxBlockSize less the
    // 32-byte header. Every chunk of a value but the last holds this many.
    public const int ChunkPayload = 65_504;

    // The most chunks one directory block holds: the largest payload of a block, in descriptors.
    public const int MostChunks = ChunkPayload / DescriptorSize;

    // The bound of one value: the most bytes of its stored encoding, MostChunks full chunks,
    // 268,173,376 bytes. A fixed rule of the format, not an option; the validation of the core states
    // it as Access.Values.ValueBound.Bytes, the same number, and RootWriter refuses a larger value as
    // the guard of the stored form.
    public const int MostValueBytes = MostChunks * ChunkPayload;

    // The layout version in the header of the blocks of a text entry, which belong to no root layout.
    private const uint NoLayout = 0;

    // How many chunks a value of this many bytes takes, for a value within MostValueBytes.
    public static int ChunkCount(int length) => (length + ChunkPayload - 1) / ChunkPayload;

    public static ValueDescriptor Read(ReadOnlySpan<byte> place) => MemoryMarshal.Read<ValueDescriptor>(place);

    public static void Write(Span<byte> place, ValueDescriptor descriptor) => MemoryMarshal.Write(place, in descriptor);

    // A value still to be placed has its length or count, but names no storage yet.
    public static void WritePending(Span<byte> place, uint length) => Write(place, new ValueDescriptor { Length = length });

    public static bool IsPending(in ValueDescriptor descriptor) =>
        descriptor.Kind == DescriptorKindEnum.Null && descriptor.Length != 0;

    // The address a descriptor of kind Block, Chunked or Text holds in its Location. Inlined, so that the
    // walks over the shared keys of the nodes of an index make no call for each key (JIT listing at full
    // optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArenaAddress AddressOf(in ValueDescriptor descriptor) =>
        new((uint)(descriptor.Location >> 32), (uint)descriptor.Location);

    // Whether the value a descriptor of a root or of a text entry names lies in chunks. A value is
    // placed by its length alone: inline or in one block up to SingleBlockLimit bytes, in chunks above,
    // a Plain value and the entry of a Dictionary text alike. RootWriter places every value so, and
    // Replication.Load.BlockImport refuses any other form on receipt, so the length decides for every
    // kind and the kind need not be read; a List whose elements hold no descriptor lies as such a value, by
    // its bytes. Not for the descriptors in a directory, which name chunks, nor for a List of kind Records,
    // whose length is a number of elements and whose form its kind tells (Roots.NestedValues.LiesInChunks):
    // telling the kinds apart here would add a compare to every read of a String.
    public static bool LiesInChunks(in ValueDescriptor descriptor)
    {
        Debug.Assert(descriptor.Kind != DescriptorKindEnum.Records, "The records of a List lie in chunks by their count, not by their length.");

        return descriptor.Length > SingleBlockLimit;
    }

    // Whether the descriptor names bytes out of line, in blocks the wire carries after the root payload:
    // a value of a root in blocks of its own, or the entry of a Dictionary text.
    public static bool LiesOutOfLine(in ValueDescriptor descriptor) =>
        descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked or DescriptorKindEnum.Text;

    // Allocates the blocks of a value of length bytes, more than lie inline, and fills them from the
    // source: one block of kind Value up to SingleBlockLimit bytes, else a directory and its chunks.
    // Every block carries the root's handle and the layout version; a chunk names its directory as
    // its parent at once, while the parent of the value block or of the directory is the root block,
    // which does not exist yet: RootWriter.Complete names it with Adopt. The caller has checked that
    // the value is within MostValueBytes, so that it needs no more chunks than MostChunks.
    //
    // A value is stored whole or not at all. When it is stored, the descriptor in place names it and
    // the method returns true. When the arena refuses a block for its budget, the method returns
    // false, and when the native allocator fails, its OutOfMemoryException passes through; either way
    // the blocks already allocated for the value are freed first and no descriptor is returned. The
    // writer therefore never holds a half-stored value: a descriptor in its image names a whole value
    // or nothing.
    //
    // The blocks are prepared (Arena.TryAllocatePrepared).
    public static bool TryStore<TSource>(Arena arena, RootHandle root, uint layoutVersion, ArenaAddress parent, uint offset, int length, ref TSource source, out ValueDescriptor descriptor)
        where TSource : IValueSource, allows ref struct =>
        TryStore(arena, root, layoutVersion, parent, offset, sharedText: false, length, ref source, out descriptor);

    // Allocates the blocks of the entry of a Dictionary text of length bytes, at least one, and fills
    // them from the source, by the placement of TryStore: every block of kind SharedText, its parent
    // none, a chunk's parent its directory, and its header naming the slot of the entry in the text table
    // where the block of a root names its root, as a handle of generation 0, which no lifetime of a root
    // has (RootTable); TextSlotOf reads it. Returns a Text descriptor with the
    // length and the address of the block or the directory. Stored whole or not at all, as TryStore.
    public static bool TryStoreText(Arena arena, uint slot, int length, ref ValueSource source, out ValueDescriptor descriptor) =>
        TryStore(arena, new RootHandle(0, slot), NoLayout, default, 0, sharedText: true, length, ref source, out descriptor);

    // The slot of the text entry whose first block is at the address, as TryStoreText wrote it into the
    // header of the block.
    public static uint TextSlotOf(Arena arena, ArenaAddress address) => arena.Header(address).Root.Slot;

    // Frees every block of a value a root owns, a Block or a Chunked descriptor: its value block, or its
    // chunks and then their directory. A directory entry that is still zero names no chunk: TryStore
    // stopped before it allocated that chunk, and frees the rest of the value through this method.
    public static void Free(Arena arena, in ValueDescriptor descriptor) =>
        FreeBlocks(arena, AddressOf(descriptor), descriptor.Kind == DescriptorKindEnum.Chunked);

    // Frees the blocks of the entry of a Dictionary text of length bytes at the address: for the text
    // table, when the last reference to the entry went.
    public static void FreeText(Arena arena, ArenaAddress address, uint length) =>
        FreeBlocks(arena, address, length > SingleBlockLimit);

    // The bytes of the slots that a value of this many bytes, more than lie inline, takes out of line:
    // the placement of TryStore, its value block, or its directory and its chunks, each the whole slot of
    // its size class, which a text entry follows as well.
    public static long SlotBytesOf(int length)
    {
        if (length <= SingleBlockLimit)
        {
            return Arena.BlockSizeFor(length);
        }

        var chunks = ChunkCount(length);
        var last = length - ((chunks - 1) * ChunkPayload);

        return Arena.BlockSizeFor(chunks * DescriptorSize)
            + ((long)(chunks - 1) * Arena.BlockSizeFor(ChunkPayload))
            + Arena.BlockSizeFor(last);
    }

    // Publishes every block of a value that TryStore stored as prepared, a Block or a Chunked
    // descriptor: its value block, or its chunks and their directory (Arena.Publish).
    public static void Publish(Arena arena, in ValueDescriptor descriptor) =>
        PublishBlocks(arena, AddressOf(descriptor), descriptor.Kind == DescriptorKindEnum.Chunked);

    // Publishes the blocks of the entry of a Dictionary text of length bytes that TryStoreText stored as
    // prepared, when the first root that refers to it is published.
    public static void PublishText(Arena arena, ArenaAddress address, uint length) =>
        PublishBlocks(arena, address, length > SingleBlockLimit);

    // Retires every block of a value of a published root, a Block or a Chunked descriptor: its value
    // block, or its chunks and their directory (Arena.Retire). Nothing is read but the directory, which
    // stays as it is, so the blocks can still be read and freed afterwards.
    public static void Retire(Arena arena, in ValueDescriptor descriptor) =>
        RetireBlocks(arena, AddressOf(descriptor), descriptor.Kind == DescriptorKindEnum.Chunked);

    // Retires the one block at the address, or the chunks its directory names and the directory, as Retire
    // does: for the blocks of the records of a List as for a value.
    public static void RetireBlocks(Arena arena, ArenaAddress address, bool chunks)
    {
        if (!chunks)
        {
            arena.Retire(address);

            return;
        }

        var entries = arena.Payload(address);

        for (var entryOffset = 0; entryOffset < entries.Length; entryOffset += DescriptorSize)
        {
            arena.Retire(AddressOf(ResidentDescriptor.Read(entries[entryOffset..])));
        }

        arena.Retire(address);
    }

    // The bytes of a value that lies in one piece: inline in the root block, whose payload is given,
    // or in its one value block or the one block of its text entry.
    public static ReadOnlySpan<byte> Contiguous(Arena arena, scoped in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload) =>
        descriptor.Kind == DescriptorKindEnum.Inline
            ? rootPayload.Slice((int)descriptor.Location - BlockHeader.Size, (int)descriptor.Length)
            : arena.Payload(AddressOf(descriptor))[..(int)descriptor.Length];

    // The entries of the directory of a value that lies in chunks, one descriptor for each chunk.
    public static ReadOnlySpan<byte> Directory(Arena arena, scoped in ValueDescriptor descriptor) =>
        arena.Payload(AddressOf(descriptor));

    // The bytes of one chunk, by its entry in the directory.
    public static ReadOnlySpan<byte> Chunk(Arena arena, ReadOnlySpan<byte> entries, int chunk)
    {
        var entry = ResidentDescriptor.Read(entries[(chunk * DescriptorSize)..]);

        return arena.Payload(AddressOf(entry))[..(int)entry.Length];
    }

    // Copies the bytes of a value, wherever it lies, into a destination of its length.
    public static void CopyTo(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, Span<byte> destination)
    {
        if (!LiesInChunks(descriptor))
        {
            Contiguous(arena, descriptor, rootPayload).CopyTo(destination);

            return;
        }

        var entries = Directory(arena, descriptor);
        var copied = 0;

        for (var chunk = 0; chunk < entries.Length / DescriptorSize; chunk++)
        {
            var bytes = Chunk(arena, entries, chunk);

            bytes.CopyTo(destination[copied..]);
            copied += bytes.Length;
        }
    }

    private static bool TryStore<TSource>(Arena arena, RootHandle owner, uint layoutVersion, ArenaAddress parent, uint offset, bool sharedText, int length, ref TSource source, out ValueDescriptor descriptor)
        where TSource : IValueSource, allows ref struct
    {
        descriptor = default;
        var valueKind = sharedText ? BlockKindEnum.SharedText : BlockKindEnum.Value;
        var directoryKind = sharedText ? BlockKindEnum.SharedText : BlockKindEnum.ChunkDirectory;
        var chunkKind = sharedText ? BlockKindEnum.SharedText : BlockKindEnum.Chunk;

        if (length <= SingleBlockLimit)
        {
            // One block: it is allocated or, when the allocation fails, nothing is.
            if (!TryAllocate(arena, length, valueKind, owner, layoutVersion, parent, offset, out var block))
            {
                return false;
            }

            source.Fill(arena.Payload(block));
            descriptor = new ValueDescriptor { Kind = sharedText ? DescriptorKindEnum.Text : DescriptorKindEnum.Block, Length = (uint)length, Location = block.Value };

            return true;
        }

        var chunks = ChunkCount(length);

        if (!TryAllocate(arena, chunks * DescriptorSize, directoryKind, owner, layoutVersion, parent, offset, out var directory))
        {
            return false;
        }

        var stored = new ValueDescriptor { Kind = sharedText ? DescriptorKindEnum.Text : DescriptorKindEnum.Chunked, Length = (uint)length, Location = directory.Value };

        // The payload of a block stays where it is while the block is allocated, so the span over the
        // entries holds while the chunks are allocated. The entries start zero, so that the free of a
        // value whose chunks are not all allocated yet frees those that are.
        var entries = arena.Payload(directory);

        entries.Clear();

        try
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var chunkLength = Math.Min(ChunkPayload, length - chunk * ChunkPayload);
                var entryOffset = chunk * DescriptorSize;

                if (!TryAllocate(arena, chunkLength, chunkKind, owner, layoutVersion, directory, (uint)(BlockHeader.Size + entryOffset), out var block))
                {
                    FreeBlocks(arena, directory, chunks: true);

                    return false;
                }

                ResidentDescriptor.Write(entries[entryOffset..], new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)chunkLength, Location = block.Value });
                source.Fill(arena.Payload(block));
            }
        }
        catch (OutOfMemoryException)
        {
            // The chunks allocated so far and the directory go before the failure passes on.
            FreeBlocks(arena, directory, chunks: true);

            throw;
        }

        descriptor = stored;

        return true;
    }

    // Frees the one block at the address, or the chunks its directory names and then the directory. A
    // directory entry that is still zero names no chunk (TryStore): for the blocks of the records of a List
    // as for a value.
    public static void FreeBlocks(Arena arena, ArenaAddress address, bool chunks)
    {
        if (!chunks)
        {
            arena.Free(address);

            return;
        }

        var entries = arena.Payload(address);

        for (var entryOffset = 0; entryOffset < entries.Length; entryOffset += DescriptorSize)
        {
            var chunk = ResidentDescriptor.Read(entries[entryOffset..]);

            if (chunk.Location != 0)
            {
                arena.Free(AddressOf(chunk));
            }
        }

        arena.Free(address);
    }

    // Publishes the one block at the address, or the chunks its directory names and the directory: for the
    // blocks of the records of a List as for a value.
    public static void PublishBlocks(Arena arena, ArenaAddress address, bool chunks)
    {
        if (chunks)
        {
            var entries = arena.Payload(address);

            for (var entryOffset = 0; entryOffset < entries.Length; entryOffset += DescriptorSize)
            {
                arena.Publish(AddressOf(ResidentDescriptor.Read(entries[entryOffset..])));
            }
        }

        arena.Publish(address);
    }

    // Allocates a prepared block of a root or of a text entry and completes its header: the
    // arena writes the owner, the kind and the length; the layout version, the parent and the offset of
    // the descriptor in the parent are written here. For the blocks of the records of a List as for a value.
    public static bool TryAllocate(
        Arena arena,
        int payloadLength,
        BlockKindEnum kind,
        RootHandle owner,
        uint layoutVersion,
        ArenaAddress parent,
        uint descriptorOffset,
        out ArenaAddress address)
    {
        if (!arena.TryAllocatePrepared(payloadLength, kind, owner, out address))
        {
            return false;
        }

        ref var header = ref arena.Header(address);

        header.LayoutVersion = layoutVersion;
        header.Parent = parent;
        header.DescriptorOffset = descriptorOffset;

        return true;
    }
}
