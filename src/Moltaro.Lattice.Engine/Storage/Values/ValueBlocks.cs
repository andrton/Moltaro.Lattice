using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The blocks of one String or Binary value that does not lie inline: one block of kind Value, or a
// directory of kind ChunkDirectory with its chunks of kind Chunk, as RootLayout places them. What a
// root owns besides its root block is reached through these descriptors and nothing else, so storing,
// reading, adopting and freeing a value all start from its descriptor.
//
// A descriptor lies in a root block, or in a directory, at an offset the packed layout gives, which
// need not be aligned: it is always read and written as 16 bytes, never through a reference.
//
// A directory holds one descriptor for each chunk, in the order of the value: kind Block, the bytes
// of the chunk, the relative address of the chunk. Its payload is 16 bytes for each chunk and nothing
// else, so its length tells how many chunks the value has.
internal static class ValueBlocks
{
    // The bytes of a ValueDescriptor, in a root block and in a directory.
    public const int DescriptorSize = 16;

    // The most chunks one directory block holds: the largest payload of a block, in descriptors.
    public const int MostChunks = RootLayout.ChunkPayload / DescriptorSize;

    // The bound of one value: the most bytes of its stored encoding, MostChunks full chunks,
    // 268,173,376 bytes. A fixed rule of the format, not an option (decision O19 of Epic 1); the
    // validation of the core states it as Access.Values.ValueBound.Bytes, the same number, and
    // RootWriter refuses a larger value as the guard of the stored form.
    public const int MostValueBytes = MostChunks * RootLayout.ChunkPayload;

    // How many chunks a value of this many bytes takes, for a value within MostValueBytes.
    public static int ChunkCount(int length) => (length + RootLayout.ChunkPayload - 1) / RootLayout.ChunkPayload;

    public static ValueDescriptor Read(ReadOnlySpan<byte> place) => MemoryMarshal.Read<ValueDescriptor>(place);

    public static void Write(Span<byte> place, ValueDescriptor descriptor) => MemoryMarshal.Write(place, in descriptor);

    // The address a descriptor of kind Block or Chunked holds in its Location.
    public static ArenaAddress AddressOf(in ValueDescriptor descriptor) =>
        new((uint)(descriptor.Location >> 32), (uint)descriptor.Location);

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
    // the blocks already allocated for the value are freed first and place is left as it was. The
    // writer therefore never holds a half-stored value: a descriptor in its image names a whole value
    // or nothing (brief of S05, "The size of an entity").
    //
    // The blocks are prepared when prepared is true (Arena.TryAllocatePrepared), and live otherwise.
    public static bool TryStore(Arena arena, RootHandle root, uint layoutVersion, bool prepared, int length, ref ValueSource source, scoped Span<byte> place)
    {
        if (length <= RootLayout.SingleBlockLimit)
        {
            // One block: it is allocated or, when the allocation fails, nothing is.
            if (!TryAllocate(arena, length, BlockKindEnum.Value, root, layoutVersion, prepared, default, 0, out var block))
            {
                return false;
            }

            source.Fill(arena.Payload(block));
            Write(place, new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)length, Location = block.Value });

            return true;
        }

        var chunks = ChunkCount(length);

        if (!TryAllocate(arena, chunks * DescriptorSize, BlockKindEnum.ChunkDirectory, root, layoutVersion, prepared, default, 0, out var directory))
        {
            return false;
        }

        var stored = new ValueDescriptor { Kind = DescriptorKindEnum.Chunked, Length = (uint)length, Location = directory.Value };

        // The payload of a block stays where it is while the block is allocated, so the span over the
        // entries holds while the chunks are allocated. The entries start zero, so that Free, given the
        // descriptor of a value whose chunks are not all allocated yet, frees those that are.
        var entries = arena.Payload(directory);

        entries.Clear();

        try
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var chunkLength = Math.Min(RootLayout.ChunkPayload, length - chunk * RootLayout.ChunkPayload);
                var entryOffset = chunk * DescriptorSize;

                if (!TryAllocate(arena, chunkLength, BlockKindEnum.Chunk, root, layoutVersion, prepared, directory, (uint)(BlockHeader.Size + entryOffset), out var block))
                {
                    Free(arena, stored);

                    return false;
                }

                Write(entries[entryOffset..], new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)chunkLength, Location = block.Value });
                source.Fill(arena.Payload(block));
            }
        }
        catch (OutOfMemoryException)
        {
            // The chunks allocated so far and the directory go before the failure passes on.
            Free(arena, stored);

            throw;
        }

        Write(place, stored);

        return true;
    }

    // Names the root block as the parent of the value block or the directory of a value, with the
    // offset of its descriptor in the root block. A value inline or null has no block to adopt.
    public static void Adopt(Arena arena, in ValueDescriptor descriptor, ArenaAddress root, int descriptorOffset)
    {
        if (descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked)
        {
            ref var header = ref arena.Header(AddressOf(descriptor));

            header.Parent = root;
            header.DescriptorOffset = (uint)descriptorOffset;
        }
    }

    // Frees every block of a value: its value block, or its chunks and then their directory. A
    // directory entry that is still zero names no chunk: TryStore stopped before it allocated that
    // chunk, and frees the rest of the value through this method. A value inline or null owns no
    // block, and text of kind Text belongs to its text entry, not to the root.
    public static void Free(Arena arena, in ValueDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Block:
                arena.Free(AddressOf(descriptor));
                break;

            case DescriptorKindEnum.Chunked:
                var directory = AddressOf(descriptor);
                var entries = arena.Payload(directory);

                for (var entryOffset = 0; entryOffset < entries.Length; entryOffset += DescriptorSize)
                {
                    var chunk = Read(entries[entryOffset..]);

                    if (chunk.Location != 0)
                    {
                        arena.Free(AddressOf(chunk));
                    }
                }

                arena.Free(directory);
                break;
        }
    }

    // Publishes every block of a value that TryStore stored as prepared: its value block, or its
    // chunks and their directory (Arena.Publish). A value inline or null owns no block.
    public static void Publish(Arena arena, in ValueDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Block:
                arena.Publish(AddressOf(descriptor));
                break;

            case DescriptorKindEnum.Chunked:
                var directory = AddressOf(descriptor);
                var entries = arena.Payload(directory);

                for (var entryOffset = 0; entryOffset < entries.Length; entryOffset += DescriptorSize)
                {
                    arena.Publish(AddressOf(Read(entries[entryOffset..])));
                }

                arena.Publish(directory);
                break;
        }
    }

    // The bytes of a value that lies in one piece: inline in the root block, whose payload is given,
    // or in its one value block.
    public static ReadOnlySpan<byte> Contiguous(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload) =>
        descriptor.Kind == DescriptorKindEnum.Inline
            ? rootPayload.Slice((int)descriptor.Location - BlockHeader.Size, (int)descriptor.Length)
            : arena.Payload(AddressOf(descriptor))[..(int)descriptor.Length];

    // The entries of the directory of a chunked value, one descriptor for each chunk.
    public static ReadOnlySpan<byte> Directory(Arena arena, in ValueDescriptor descriptor) =>
        arena.Payload(AddressOf(descriptor));

    // The bytes of one chunk, by its entry in the directory.
    public static ReadOnlySpan<byte> Chunk(Arena arena, ReadOnlySpan<byte> entries, int chunk)
    {
        var entry = Read(entries[(chunk * DescriptorSize)..]);

        return arena.Payload(AddressOf(entry))[..(int)entry.Length];
    }

    // Whether the bytes of a value from offset on begin with the given bytes, wherever the value
    // lies; the caller has checked that offset plus their length is within the value. Every chunk
    // but the last holds ChunkPayload bytes, so the chunk of an offset is found by arithmetic.
    public static bool RangeEquals(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, long offset, ReadOnlySpan<byte> bytes)
    {
        if (descriptor.Kind != DescriptorKindEnum.Chunked)
        {
            return Contiguous(arena, descriptor, rootPayload).Slice((int)offset, bytes.Length).SequenceEqual(bytes);
        }

        var entries = Directory(arena, descriptor);
        var chunk = (int)(offset / RootLayout.ChunkPayload);
        var within = (int)(offset % RootLayout.ChunkPayload);

        while (!bytes.IsEmpty)
        {
            var stored = Chunk(arena, entries, chunk)[within..];
            var length = Math.Min(stored.Length, bytes.Length);

            if (!stored[..length].SequenceEqual(bytes[..length]))
            {
                return false;
            }

            bytes = bytes[length..];
            chunk++;
            within = 0;
        }

        return true;
    }

    // Copies the bytes of a value, wherever it lies, into a destination of its length.
    public static void CopyTo(Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> rootPayload, Span<byte> destination)
    {
        if (descriptor.Kind != DescriptorKindEnum.Chunked)
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

    // Allocates a block of a root, prepared or live, and completes its header: the arena writes the
    // root, the kind and the length; the layout version, the parent and the offset of the descriptor
    // in the parent are written here.
    private static bool TryAllocate(
        Arena arena,
        int payloadLength,
        BlockKindEnum kind,
        RootHandle root,
        uint layoutVersion,
        bool prepared,
        ArenaAddress parent,
        uint descriptorOffset,
        out ArenaAddress address)
    {
        var allocated = prepared
            ? arena.TryAllocatePrepared(payloadLength, kind, root, out address)
            : arena.TryAllocate(payloadLength, kind, root, out address);

        if (!allocated)
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
