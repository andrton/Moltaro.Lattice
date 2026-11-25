using System.Runtime.InteropServices;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// What the ownership audit does with each descriptor the walk over one live root reaches (OwnershipAudit,
// IStructureVisitor). It finds every block the descriptor names from the descriptor alone, by the forms of
// DescriptorKindEnum, and checks that block's header against the place it was reached from: the handle of
// the root, the block that holds the descriptor as its parent, the offset of the descriptor, its kind and
// its length. It records every block in the set of the audit, which refuses an address it holds already, so
// a block reached twice, from one root or from two, fails the audit; and it adds up the bytes of the slots
// of the blocks it reached, for the audit to compare with the counters of the arena. A descriptor of kind
// Text holds one reference to an entry of the text table, which it counts; an inline value lies inside the
// block that holds its descriptor.
internal struct AuditVisitor : IStructureVisitor
{
    private readonly RootHandle _root;

    private readonly HashSet<ulong> _blocks;

    private readonly Dictionary<ulong, (uint Length, long References)> _texts;

    public AuditVisitor(RootHandle root, HashSet<ulong> blocks, Dictionary<ulong, (uint Length, long References)> texts)
    {
        _root = root;
        _blocks = blocks;
        _texts = texts;
    }

    // The bytes of the slots of the blocks this visitor reached, the root block not included.
    public long SlotBytes { get; private set; }

    // The bytes of the payloads of those blocks, and of the texts the Text descriptors name with the 16 bytes
    // the directory of a text in chunks holds for each chunk: what RootReader.EntityBytes counts beside the
    // root block.
    public long StoredBytes { get; private set; }

    public readonly bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset) => true;

    public readonly void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) { }

    public void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
    {
        var address = ValueBlocks.AddressOf(descriptor);

        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Null:
                Assert.Equal((0u, 0ul), (descriptor.Length, descriptor.Location));
                break;

            case DescriptorKindEnum.Inline:
                Assert.InRange(descriptor.Location + descriptor.Length, (ulong)BlockHeader.Size, BlockHeader.Size + (ulong)arena.Header(holder).PayloadLength);
                break;

            case DescriptorKindEnum.Block:
                Own(arena, address, holder, offset, BlockKindEnum.Value, descriptor.Length);
                break;

            case DescriptorKindEnum.Chunked:
                OwnChunks(arena, address, holder, offset, ValueBlocks.ChunkCount((int)descriptor.Length));
                break;

            case DescriptorKindEnum.Records:
                OwnRecords(arena, descriptor, holder, offset, element!);
                break;

            case DescriptorKindEnum.Lookup:
                Own(arena, address, holder, offset, BlockKindEnum.Lookup, length: null);
                // A block after its header and entries descriptor holds 8,186 slots.
                // At a load of 70 percent it can index at most 5,730 entries.
                if (descriptor.Length > 5_730)
                {
                    var chunks = MemoryMarshal.Cast<byte, ArenaAddress>(arena.Payload(address)[16..]);
                    for (var chunk = 0; chunk < chunks.Length; chunk++)
                    {
                        Own(arena, chunks[chunk], address, 48 + chunk * 8, BlockKindEnum.Chunk, length: null);
                    }
                }

                break;

            case DescriptorKindEnum.Text:
                var text = _texts.GetValueOrDefault(address.Value, (descriptor.Length, 0));

                Assert.Equal(text.Length, descriptor.Length);
                _texts[address.Value] = (text.Length, text.References + 1);
                StoredBytes += descriptor.Length + (ValueBlocks.LiesInChunks(descriptor) ? (long)ValueBlocks.ChunkCount((int)descriptor.Length) * ValueBlocks.DescriptorSize : 0);
                break;

            default:
                Assert.Fail($"A descriptor at {offset} of the block {holder} has the kind {descriptor.Kind}.");
                break;
        }
    }

    // The block of the records of a List, or the directory of their chunks and the chunks, each holding at
    // least the records it has to hold.
    private void OwnRecords(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout element)
    {
        var count = (long)descriptor.Length;

        if (count == 0)
        {
            Assert.Equal(0ul, descriptor.Location);

            return;
        }

        var address = ValueBlocks.AddressOf(descriptor);

        if (count <= element.RecordsPerChunk)
        {
            Own(arena, address, holder, offset, BlockKindEnum.Value, length: null);
            Assert.True(arena.Header(address).PayloadLength >= count * element.Size, $"The block of {count} records of {element.Size} bytes holds {arena.Header(address).PayloadLength}.");

            return;
        }

        var chunks = (int)((count + element.RecordsPerChunk - 1) / element.RecordsPerChunk);

        OwnChunks(arena, address, holder, offset, chunks);

        var entries = arena.Payload(address);

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var records = Math.Min(element.RecordsPerChunk, count - ((long)chunk * element.RecordsPerChunk));
            var length = arena.Header(ValueBlocks.AddressOf(ResidentDescriptor.Read(entries[(chunk * ValueBlocks.DescriptorSize)..]))).PayloadLength;

            Assert.True(length >= records * element.Size, $"Chunk {chunk} of {records} records of {element.Size} bytes holds {length}.");
        }
    }

    // A directory of that many chunks, owned by the holder, and each chunk owned by the directory.
    private void OwnChunks(Arena arena, ArenaAddress directory, ArenaAddress holder, int offset, int chunks)
    {
        Own(arena, directory, holder, offset, BlockKindEnum.ChunkDirectory, (uint)(chunks * ValueBlocks.DescriptorSize));

        var entries = arena.Payload(directory);

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var entry = ResidentDescriptor.Read(entries[(chunk * ValueBlocks.DescriptorSize)..]);

            Assert.Equal(DescriptorKindEnum.Block, entry.Kind);
            Own(arena, ValueBlocks.AddressOf(entry), directory, BlockHeader.Size + (chunk * ValueBlocks.DescriptorSize), BlockKindEnum.Chunk, entry.Length);
        }
    }

    // One block, reached from the parent at the offset: its header names this root, that parent and that
    // offset, and its kind and, when it is given, its length; no other place reached it before.
    private void Own(Arena arena, ArenaAddress address, ArenaAddress parent, int offset, BlockKindEnum kind, uint? length)
    {
        Assert.True(_blocks.Add(address.Value), $"The block {address} is reached twice.");

        ref var header = ref arena.Header(address);

        Assert.Equal(_root, header.Root);
        Assert.Equal(parent, header.Parent);
        Assert.Equal((uint)offset, header.DescriptorOffset);
        Assert.Equal(kind, header.Kind);

        if (length is { } expected)
        {
            Assert.Equal(expected, header.PayloadLength);
        }

        SlotBytes += Arena.BlockSizeFor((int)header.PayloadLength);
        StoredBytes += header.PayloadLength;
    }
}
