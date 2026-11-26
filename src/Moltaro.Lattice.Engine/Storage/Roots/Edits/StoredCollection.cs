using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// The entries of the immediate predecessor, reached under the writing lock.
// A key probe uses the resident lookup and an entry can be copied without
// decoding the rest of its collection.
internal readonly struct StoredCollection
{
    private readonly Arena _arena;
    private readonly StructureLayout _element;
    private readonly LatticeStringEncodingEnum _encoding;
    private readonly ArenaAddress _holder;
    private readonly ArenaAddress _entriesHolder;
    private readonly ValueDescriptor _collection;
    private readonly ValueDescriptor _entries;

    public StoredCollection(Arena arena, StructureLayout element, LatticeStringEncodingEnum encoding, ArenaAddress holder, in ValueDescriptor collection)
    {
        _arena = arena;
        _element = element;
        _encoding = encoding;
        _holder = holder;
        _collection = collection;
        _entriesHolder = collection.Kind == DescriptorKindEnum.Lookup ? ValueBlocks.AddressOf(collection) : holder;
        _entries = collection.Kind == DescriptorKindEnum.Lookup ? CollectionLookup.Entries(arena, collection) : collection;
    }

    public int Count => _element.StoredEntryCount(_entries);

    public bool Hashed => _collection.Kind == DescriptorKindEnum.Lookup;

    public ValueDescriptor Descriptor => _collection;

    public int Find(object? key) =>
        CollectionLookup.Find(_arena, _element, _collection, _arena.Payload(_holder), _encoding, IndexValue.Of(key));

    public string Spelling(int ordinal, MemoryLease? lease)
    {
        var keys = new CollectionKeys(_arena, _element, _collection, _arena.Payload(_holder), _encoding);
        return keys.Spelling(ordinal, lease);
    }

    public (ArenaAddress Address, int Offset) Record(int ordinal)
    {
        if (!NestedValues.LiesInChunks(_entries, _element))
        {
            return (ValueBlocks.AddressOf(_entries), BlockHeader.Size + ordinal * _element.Size);
        }

        var chunk = ordinal / _element.RecordsPerChunk;
        var within = ordinal % _element.RecordsPerChunk;
        var directory = ValueBlocks.Directory(_arena, _entries);
        var entry = ResidentDescriptor.Read(directory[(chunk * ValueBlocks.DescriptorSize)..]);
        return (ValueBlocks.AddressOf(entry), BlockHeader.Size + within * _element.Size);
    }

    public void CopyPacked(int ordinal, Span<byte> record)
    {
        var start = ordinal * _element.Size;
        if (!ValueBlocks.LiesInChunks(_entries))
        {
            ValueBlocks.Contiguous(_arena, _entries, _arena.Payload(_entriesHolder)).Slice(start, record.Length).CopyTo(record);
            return;
        }

        var directory = ValueBlocks.Directory(_arena, _entries);
        var chunk = start / ValueBlocks.ChunkPayload;
        var within = start % ValueBlocks.ChunkPayload;
        var bytes = ValueBlocks.Chunk(_arena, directory, chunk)[within..];
        var first = Math.Min(bytes.Length, record.Length);
        bytes[..first].CopyTo(record);
        if (first < record.Length)
        {
            ValueBlocks.Chunk(_arena, directory, chunk + 1)[..(record.Length - first)].CopyTo(record[first..]);
        }
    }
}
