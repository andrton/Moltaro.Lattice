using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Protected access to collection entries and to the first member of each for a lookup.
// Packed records may cross a byte chunk. EntryParts keeps both slices, and key comparison
// uses scratch only for a split key. A record with descriptors lies whole in a block and
// its inline locations are relative to that block. See docs/design/arena-protocol.md.
internal readonly ref struct CollectionKeys : ICollectionKeySource
{
    private readonly Arena _arena;

    private readonly StructureLayout _entry;

    private readonly ValueDescriptor _entries;

    private readonly ReadOnlySpan<byte> _payload;

    private readonly LatticeStringEncodingEnum _encoding;

    public CollectionKeys(Arena arena, StructureLayout entry, scoped in ValueDescriptor descriptor, ReadOnlySpan<byte> payload, LatticeStringEncodingEnum encoding)
        : this(arena, entry, descriptor, payload, encoding, CollectionLookup.OpenPayload(arena, descriptor))
    {
    }

    public CollectionKeys(Arena arena, StructureLayout entry, scoped in ValueDescriptor descriptor, ReadOnlySpan<byte> payload,
        LatticeStringEncodingEnum encoding, ReadOnlySpan<byte> lookupPayload)
    {
        _arena = arena;
        _entry = entry;
        _encoding = encoding;
        if (descriptor.Kind == DescriptorKindEnum.Lookup)
        {
            _payload = lookupPayload;
            _entries = ResidentDescriptor.ReadAligned(lookupPayload);
        }
        else
        {
            _payload = payload;
            _entries = descriptor;
        }
    }

    public int Count => _entry.StoredEntryCount(_entries);

    public int Width => Key.Type == StoredTypeEnum.String ? 0 : Key.Width;

    private ref readonly StoredField Key => ref _entry.Place(0);

    private LatticeValueDeclaration Declaration => _entry.DeclarationOf(0);

    public void Encode(in IndexValue probe, Span<byte> destination)
    {
        if (!probe.IsNull && Key.Type != StoredTypeEnum.String)
        {
            StoredScalars.WriteBound(destination, Key.Type, probe);
        }
    }

    public uint Hash(in IndexValue probe, ReadOnlySpan<byte> fixedProbe)
    {
        var seed = _arena.HashSeed;
        if (probe.IsNull)
        {
            return NullHash(seed);
        }

        return Key.Type == StoredTypeEnum.String
            ? ValueComparer.TextHash(Declaration, probe.Text.AsSpan(), seed)
            : ValueComparer.FixedHash(Key.Type, fixedProbe, seed);
    }

    public uint Hash(int ordinal)
    {
        var seed = _arena.HashSeed;
        if (IsNull(ordinal))
        {
            return NullHash(seed);
        }

        Span<byte> scratch = stackalloc byte[16];
        return Key.Type == StoredTypeEnum.String
            ? ValueComparer.TextHash(Declaration, Text(ordinal), seed)
            : ValueComparer.FixedHash(Key.Type, Bytes(ordinal, Key.Offset, Key.Width, scratch), seed);
    }

    public bool Equal(int left, int right)
    {
        var leftNull = IsNull(left);
        var rightNull = IsNull(right);
        if (leftNull || rightNull)
        {
            return leftNull == rightNull;
        }

        if (Key.Type == StoredTypeEnum.String)
        {
            return ValueComparer.CompareText(Text(left), Text(right), Declaration.TextComparison!) == 0;
        }

        Span<byte> leftScratch = stackalloc byte[16];
        Span<byte> rightScratch = stackalloc byte[16];
        return ValueComparer.CompareStored(Key.Type, Bytes(left, Key.Offset, Key.Width, leftScratch), Bytes(right, Key.Offset, Key.Width, rightScratch)) == 0;
    }

    public bool Equal(int ordinal, in IndexValue probe, ReadOnlySpan<byte> fixedProbe)
    {
        var isNull = IsNull(ordinal);
        if (isNull || probe.IsNull)
        {
            return isNull == probe.IsNull;
        }

        if (Key.Type == StoredTypeEnum.String)
        {
            return ValueComparer.TextEquals(Text(ordinal), Declaration, probe.Text.AsSpan());
        }

        Span<byte> scratch = stackalloc byte[16];
        return ValueComparer.CompareStored(Key.Type, Bytes(ordinal, Key.Offset, Key.Width, scratch), fixedProbe) == 0;
    }

    // The spelling of a key that the lookup found by the schema comparison.
    public string Spelling(int ordinal, MemoryLease? lease = null)
    {
        var payload = RecordPayload(ordinal, out var start);
        var text = ResidentDescriptor.Read(payload[(start + Key.Offset)..]);
        return StoredText.Read(_arena, text, payload, _encoding, lease);
    }

    public ReadOnlySpan<byte> EntryParts(int ordinal, out ReadOnlySpan<byte> tail, out int start)
    {
        if (_entry.HoldsDescriptors)
        {
            tail = default;
            return RecordPayload(ordinal, out start);
        }

        start = 0;
        return PackedParts(ordinal * _entry.Size, _entry.Size, out tail);
    }

    public static uint NullHash(ulong seed) => ValueComparer.IntegerHash(-1, seed);

    private bool IsNull(int ordinal)
    {
        if (!Key.IsNullable)
        {
            return false;
        }

        Span<byte> scratch = stackalloc byte[1];
        var bits = Bytes(ordinal, Key.NullBit / 8, 1, scratch);
        return (bits[0] & (1 << (Key.NullBit % 8))) != 0;
    }

    private StoredTextReader Text(int ordinal)
    {
        var payload = RecordPayload(ordinal, out var start);
        var text = ResidentDescriptor.Read(payload[(start + Key.Offset)..]);
        return new StoredTextReader(_arena, text, payload, _encoding);
    }

    private ReadOnlySpan<byte> Bytes(int ordinal, int offset, int length, Span<byte> scratch)
    {
        if (_entry.HoldsDescriptors)
        {
            var payload = RecordPayload(ordinal, out var start);
            return payload.Slice(start + offset, length);
        }

        var part = PackedParts(ordinal * _entry.Size + offset, length, out var tail);
        if (tail.IsEmpty)
        {
            return part;
        }

        part.CopyTo(scratch);
        tail.CopyTo(scratch[part.Length..]);
        return scratch[..length];
    }

    private ReadOnlySpan<byte> PackedParts(int at, int length, out ReadOnlySpan<byte> tail)
    {
        tail = default;
        if (!ValueBlocks.LiesInChunks(_entries))
        {
            return ValueBlocks.Contiguous(_arena, _entries, _payload).Slice(at, length);
        }

        var chunks = ValueBlocks.Directory(_arena, _entries);
        var chunk = at / ValueBlocks.ChunkPayload;
        var within = at - chunk * ValueBlocks.ChunkPayload;
        var part = ValueBlocks.Chunk(_arena, chunks, chunk)[within..];
        if (part.Length >= length)
        {
            return part[..length];
        }

        tail = ValueBlocks.Chunk(_arena, chunks, chunk + 1)[..(length - part.Length)];
        return part;
    }

    private ReadOnlySpan<byte> RecordPayload(int ordinal, out int start)
    {
        if (!NestedValues.LiesInChunks(_entries, _entry))
        {
            start = ordinal * _entry.Size;
            return _arena.Payload(ValueBlocks.AddressOf(_entries));
        }

        var chunk = ordinal / _entry.RecordsPerChunk;
        start = (ordinal - chunk * _entry.RecordsPerChunk) * _entry.Size;
        return ValueBlocks.Chunk(_arena, ValueBlocks.Directory(_arena, _entries), chunk);
    }
}
