using System.Buffers.Binary;
using System.Globalization;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The keys of numbers for the tests that drive ReturnedKeys as a step of a pass does, without a database:
// an entity table of its own, over an arena of its own, gives the key type and the hash of the index
// under a fixed seed (EntityTable.IntegerHash, GuidHash, TextHash). An Int64 and a Guid key are read from
// a copy of the fixed part of a root into which they are written (RootReader with a fixed part), where
// they lie; a String key, whose text lies outside the fixed part, from a root written into the arena,
// its text the number followed by as many k as make it the length given. A key of one number is the same
// every time, and the keys of two numbers differ.
internal sealed class StandaloneKeys : IDisposable
{
    private const ulong Seed = 0x9E3779B97F4A7C15;

    private readonly string _keyType;

    private readonly Arena _arena;

    private readonly EntityTable _table;

    private readonly RootLayout _layout;

    private readonly byte[] _fixedPart;

    private readonly int _offset;

    // The code units of the text of a String key.
    private readonly int _textLength;

    // The root a String key was written into last.
    private ArenaAddress _root;

    // The keys of an entity type whose key has the type named: Int64, Guid, or String with the text of
    // the length given.
    public StandaloneKeys(string keyType, int textLength = 0)
    {
        var schema = KeySchema.Of("Utf8", "Ordinal", keyType);

        Assert.True(schema.TryGetEntityType(keyType, out var entityType));

        var owner = new object();
        var roots = new RootTable();
        var epochs = new ReadEpochs(owner);

        _keyType = keyType;
        _textLength = textLength;
        _arena = new Arena(new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize });
        _table = EntityTable.Open(entityType, schema, _arena, roots, epochs, new RetiredMemory(epochs, _arena, roots), new PositionClock(1, owner), Seed);
        _layout = _table.Layout!;
        _fixedPart = new byte[RootReader.FixedPartLength(_layout)];
        _offset = _layout.StoredFieldOf(entityType.Key).Offset - BlockHeader.Size;
    }

    // The words of a place of these keys: 1 for an Int64, 2 for a Guid or a String.
    public int WordsPerPlace => _keyType == "Int64" ? 1 : 2;

    // The root that holds the key written last: the fixed part, or for a String key the root in the arena.
    public RootReader Stored => _keyType == "String" ? new(_arena, _layout, _root) : new(_layout, _fixedPart);

    // A new set of returned keys of the entity type.
    public ReturnedKeys NewReturnedKeys() => new(_table);

    // Writes the key of the number and returns the hash the index gives it.
    public uint Write(long number)
    {
        if (_keyType == "String")
        {
            var text = string.Create(_textLength, number, static (units, value) =>
            {
                units.Fill('k');
                value.TryFormat(units, out _, default, CultureInfo.InvariantCulture);
            });
            var writer = new RootWriter(_arena, _layout, new RootHandle(1, 1));

            writer.SetString(_layout.EntityType.Key, text);
            writer.SetString(Body, string.Empty);
            _root = writer.Complete();

            return _table.TextHash(text);
        }

        if (_keyType == "Int64")
        {
            var key = unchecked(number * (long)0x9E37_79B9_7F4A_7C15);

            BinaryPrimitives.WriteInt64LittleEndian(_fixedPart.AsSpan(_offset), key);

            return _table.IntegerHash(key);
        }

        // The two halves of the Guid, as the engine holds them, both different for every number.
        var first = unchecked(number * (long)0xD1B5_4A32_D192_ED03);
        var second = unchecked((number * (long)0x8CB9_2BA7_2F3D_8DD7) + 1);

        BinaryPrimitives.WriteInt64LittleEndian(_fixedPart.AsSpan(_offset), first);
        BinaryPrimitives.WriteInt64LittleEndian(_fixedPart.AsSpan(_offset + 8), second);

        return _table.GuidHash(first, second);
    }

    public void Dispose() => _arena.Dispose();

    private LatticeField Body
    {
        get
        {
            Assert.True(_layout.EntityType.TryGetField("Body", out var body));

            return body;
        }
    }
}
