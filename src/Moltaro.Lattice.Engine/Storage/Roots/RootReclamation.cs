using Moltaro.Lattice.Schema.Compilation;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// A detached, epoch-safe root's post-order free walk. The writes lock protects every
// turn. Only addresses, layouts and positions survive a yield; no native span does.
// The FIFO keeps any later owner from freeing a borrowed block before this walk can
// inspect its backlink (docs/design/arena-protocol.md, "Ownership across partial updates").
internal sealed class RootReclamation(Arena arena)
{
    // Each nested collection can add its lookup, records directory and record block.
    // The final value can add a chunk directory and one chunk in place of that level.
    private readonly Frame[] _frames = new Frame[1 + 3 * SchemaBuilder.MaximumDepth];

    private int _depth;

    private TextReclamation _text;

    private bool _hasText;

    public void Start(RootLayout layout, ArenaAddress root)
    {
        _ = layout.RootPayload(arena, root);
        // Root layout offsets already include the header. Packed record offsets do not.
        Push(new Frame(KindEnum.Records, root, layout.Structure, 1, recordStart: 0));
    }

    public bool Step()
    {
        if (_hasText)
        {
            _hasText = !arena.Texts.ReleaseStep(ref _text);
            return false;
        }

        ref var frame = ref _frames[_depth - 1];
        switch (frame.Kind)
        {
            case KindEnum.Block:
                FreeTop(frame.Address);
                break;

            case KindEnum.ValueDirectory:
                StepDirectory(ref frame, records: false);
                break;

            case KindEnum.RecordsDirectory:
                StepDirectory(ref frame, records: true);
                break;

            case KindEnum.Records:
                StepRecords(ref frame);
                break;

            case KindEnum.Lookup:
                StepLookup(ref frame);
                break;
        }

        return _depth == 0;
    }

    public void Clear()
    {
        Array.Clear(_frames, 0, _depth);
        _depth = 0;
        _text = default;
        _hasText = false;
    }

    private void StepRecords(ref Frame frame)
    {
        if (frame.Next == frame.Count)
        {
            FreeTop(frame.Address);
            return;
        }

        var layout = frame.Layout!;
        if (frame.Descriptor < 0)
        {
            frame.Descriptor = 0;
            return;
        }

        if (frame.Descriptor == layout.DescriptorCount)
        {
            frame.Next++;
            frame.Descriptor = -1;
            return;
        }

        var index = frame.Descriptor++;
        var offset = frame.RecordStart + frame.Next * layout.Size + layout.DescriptorOffset + index * ValueBlocks.DescriptorSize;
        var descriptor = ResidentDescriptor.Read(arena.Payload(frame.Address)[(offset - BlockHeader.Size)..]);
        Inspect(descriptor, frame.Address, offset, layout.DescriptorElement(index));
    }

    private void StepDirectory(ref Frame frame, bool records)
    {
        var perChunk = records ? frame.Layout!.RecordsPerChunk : 1;
        var firstRecord = frame.Next * perChunk;
        if (firstRecord >= frame.Count)
        {
            FreeTop(frame.Address);
            return;
        }

        var chunk = ValueBlocks.ChunkAddressAt(arena, frame.Address, frame.Next++);
        if (!chunk.IsNull)
        {
            Push(records
                ? new Frame(KindEnum.Records, chunk, frame.Layout, Math.Min(perChunk, frame.Count - firstRecord))
                : new Frame(KindEnum.Block, chunk));
        }
    }

    private void StepLookup(ref Frame frame)
    {
        if (frame.Next < 0)
        {
            frame.Next = 0;
            var entries = ResidentDescriptor.ReadAligned(arena.Payload(frame.Address));
            Inspect(entries, frame.Address, CollectionLookup.EntriesOffset, frame.Layout);
            return;
        }

        if (frame.Next == frame.Count)
        {
            arena.CollectionTails?.Finish(frame.Address);
            FreeTop(frame.Address);
            return;
        }

        var chunk = CollectionLookup.ReadChunk(CollectionLookup.Chunks(arena, frame.Address), frame.Next++);
        if (!chunk.IsNull && arena.CollectionTails?.Accept(frame.Address, chunk) != true)
        {
            Push(new Frame(KindEnum.Block, chunk));
        }
    }

    private void Inspect(in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
    {
        if (!RootStorage.Owns(arena, descriptor, holder, offset))
        {
            return;
        }

        var address = ValueBlocks.AddressOf(descriptor);
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Block:
                Push(new Frame(KindEnum.Block, address));
                break;

            case DescriptorKindEnum.Chunked:
                Push(new Frame(KindEnum.ValueDirectory, address, count: arena.Payload(address).Length / ValueBlocks.DescriptorSize));
                break;

            case DescriptorKindEnum.Records when !address.IsNull:
                var kind = NestedValues.LiesInChunks(descriptor, element!) ? KindEnum.RecordsDirectory : KindEnum.Records;
                Push(new Frame(kind, address, element, (int)descriptor.Length));
                break;

            case DescriptorKindEnum.Lookup:
                var count = CollectionLookup.IsChunked(descriptor) ? CollectionLookup.Chunks(arena, address).Length : 0;
                Push(new Frame(KindEnum.Lookup, address, element, count) { Next = -1 });
                break;

            case DescriptorKindEnum.Text:
                _text = new TextReclamation((uint)descriptor.Location);
                _hasText = true;
                break;
        }
    }

    private void Push(Frame frame) => _frames[_depth++] = frame;

    private void FreeTop(ArenaAddress address)
    {
        arena.Free(address);
        _frames[--_depth] = default;
    }

    private enum KindEnum
    {
        Block,
        ValueDirectory,
        RecordsDirectory,
        Records,
        Lookup,
    }

    private struct Frame(KindEnum kind, ArenaAddress address, StructureLayout? layout = null, int count = 0, int recordStart = BlockHeader.Size)
    {
        public readonly KindEnum Kind = kind;
        public readonly ArenaAddress Address = address;
        public readonly StructureLayout? Layout = layout;
        public readonly int Count = count;
        public readonly int RecordStart = recordStart;
        public int Next;
        public int Descriptor = -1;
    }
}
