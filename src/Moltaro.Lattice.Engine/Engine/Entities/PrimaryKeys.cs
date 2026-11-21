using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// The primary keys of one entity type: the kind of its key (KeyKindEnum), the hash of a key in the primary
// index under the seed of the instance, when two keys are one key, the key a root stores, and the form of a
// key in the replication stream. Every key type of the catalog is stored; a String key is equal to
// another, and hashed, under the comparison its field declares, by the comparison of the schema
// (Comparison.ValueComparer). A String key may have Dictionary storage, as any String field may: the root
// then names the entry of its text in the text table, and the comparison reads the text there, as a read
// reads it.
//
// Made once with the table of its entity type (EntityTable.Open) and never changed: it holds a reference
// to the entity type and three scalars, none of which changes, so any thread calls it, a read inside the
// protection of reads as well as the holder of the lock of the writes, and a copy is the same keys. A
// value held by the table in a readonly field (EntityTable.Keys), so that its fields lie in the table and a
// read by key loads them as it loads the table's own, with no second object to reach; its members are
// called on that field where it lies, without a copy. Its users are the index and the reads of the table,
// the keys an enumeration returned (ReturnedKeys, which holds a copy), the order of the writes of one
// entity (Writes.PendingWrite), the conversion of a key a caller gives (Sets.KeyArgument), the loader of a
// child and the encoding of a Delete. An index whose entries name entities, such as one unique by its
// index key and the identity of the entity, takes the hash and the equality of that identity here:
// HashOf, KeysEqual and StoredKey.
internal readonly struct PrimaryKeys
{
    // The most bytes of UTF-8 one character takes: a supplementary character, written from a pair of
    // surrogates.
    private const int MostBytesOfACharacter = 4;

    private readonly LatticeEntityType _entityType;

    // Whether an integer key is of an unsigned type, which the root stores as such.
    private readonly bool _unsignedKey;

    // The encoding of the text of the database, in which a root stores the text of a String key.
    private readonly LatticeStringEncodingEnum _stringEncoding;

    // The seed of the hashes of the primary index, the one of the instance (HashOf). Kept here alone,
    // never stored, sent or shown.
    private readonly ulong _seed;

    public PrimaryKeys(LatticeEntityType entityType, LatticeStringEncodingEnum stringEncoding, ulong seed)
    {
        _entityType = entityType;
        KeyKind = KeyKindOf(entityType.Key);
        _unsignedKey = entityType.Key.Declaration.Type
            is LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.UInt16 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.UInt64;
        _stringEncoding = stringEncoding;
        _seed = seed;
    }

    public KeyKindEnum KeyKind { get; }

    // The key field of the entity type.
    public LatticeField Field => _entityType.Key;

    // The key a root stores, in the form the index takes it, hashed under the seed of this instance: for
    // the loader of a child, which builds its index from the keys it receives. The root is one the caller
    // placed and holds under the lock of the writes.
    public EntityKey StoredKey(in RootReader stored)
    {
        var field = _entityType.Key;

        switch (KeyKind)
        {
            case KeyKindEnum.Integer:
                return EntityKey.OfInteger(field.Declaration.Type, StoredInteger(stored));

            case KeyKindEnum.Guid:
                return EntityKey.OfGuid(stored.GetGuid(field));

            default:
                var text = stored.GetString(field);

                return EntityKey.OfText(text, TextHash(text));
        }
    }

    // A key of the String key type from its text, hashed under the seed of this instance: for the loader
    // of a child, which reads the text from a root it received before it places it
    // (Child.Loading.ReceivedChanges).
    public EntityKey KeyOfText(string text) => EntityKey.OfText(text, TextHash(text));

    // The bytes a key of this entity type takes in a Delete of the replication stream
    // (Replication.Transactions.TransactionFormat), in the form WriteKey writes and ReadKey reads: an
    // integer key as the 64 bits of EntityKey.Integer, a Guid key as its 16 bytes in the order of
    // Guid.TryWriteBytes, a String key as the length of its UTF-8 (UInt32) and that UTF-8; numbers
    // little-endian. The text of a key is well-formed, as the core checked it, so it has its UTF-8.
    public int KeyLength(in EntityKey key) => KeyKind switch
    {
        KeyKindEnum.Integer => sizeof(long),
        KeyKindEnum.Guid => 16,
        _ => sizeof(uint) + Encoding.UTF8.GetByteCount(key.Text!),
    };

    // Writes the key into the output, KeyLength bytes (see KeyLength). The text of a String key may be as
    // long as a String value may be, longer than one span of the output holds (the tail a master delivers
    // from, Delivery.DeliveryTail, gives spans of at most one block), so it is encoded into the spans as the
    // output gives them, each asked for room for one whole character; an integer, a Guid and the length of
    // a text fit the span asked for.
    public void WriteKey(in EntityKey key, IBufferWriter<byte> output)
    {
        switch (KeyKind)
        {
            case KeyKindEnum.Integer:
                BinaryPrimitives.WriteInt64LittleEndian(output.GetSpan(sizeof(long)), key.Integer);
                output.Advance(sizeof(long));
                break;

            case KeyKindEnum.Guid:
                key.Guid.TryWriteBytes(output.GetSpan(16));
                output.Advance(16);
                break;

            default:
                var text = key.Text!.AsSpan();

                BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(sizeof(uint)), (uint)Encoding.UTF8.GetByteCount(text));
                output.Advance(sizeof(uint));

                while (!text.IsEmpty)
                {
                    Utf8.FromUtf16(text, output.GetSpan(MostBytesOfACharacter), out var read, out var written);
                    output.Advance(written);
                    text = text[read..];
                }

                break;
        }
    }

    // Reads a key that WriteKey wrote from the input, hashed under the seed of this instance, with the
    // input advanced past it. ValidationFailed for bytes that are not a key of this entity type: too few
    // of them, an integer outside the key type, text that is not well-formed UTF-8.
    public EntityKey ReadKey(ref SequenceReader<byte> input)
    {
        switch (KeyKind)
        {
            case KeyKindEnum.Integer:
                if (!input.TryReadLittleEndian(out long integer))
                {
                    throw InvalidKey($"{input.Remaining} bytes are fewer than an integer key takes.");
                }

                var type = _entityType.Key.Declaration.Type;

                // The value the 64 bits stand for: those of a UInt64 key read as unsigned, as EntityKey
                // holds them (EntityKey.Integer).
                Int128 value = type == LatticeFieldTypeEnum.UInt64 ? unchecked((ulong)integer) : integer;

                if (!IntegerRange.Of(type).Contains(value))
                {
                    throw InvalidKey($"the integer {integer} is not a value of the key type {type}.");
                }

                return EntityKey.OfInteger(type, integer);

            case KeyKindEnum.Guid:
                Span<byte> guid = stackalloc byte[16];

                if (!input.TryCopyTo(guid))
                {
                    throw InvalidKey($"{input.Remaining} bytes are fewer than a Guid key takes.");
                }

                input.Advance(guid.Length);

                return EntityKey.OfGuid(new Guid(guid));

            default:
                if (!input.TryReadLittleEndian(out int length) || length < 0 || length > input.Remaining)
                {
                    throw InvalidKey("the bytes do not hold the text they announce.");
                }

                string text;

                try
                {
                    text = Protocol.StrictUtf8.GetString(input.UnreadSequence.Slice(0, length));
                }
                catch (DecoderFallbackException exception)
                {
                    throw InvalidKey($"its text is not well-formed UTF-8 ({exception.Message})");
                }

                input.Advance(length);

                return KeyOfText(text);
        }
    }

    // Whether two keys of the entity type are one key, as the schema compares keys: an integer and a
    // Guid by their value, a String by the comparison its field declares (Comparison.ValueComparer),
    // so that two spellings of a key compared without case are one entity for the order of the writes
    // (Writes.PendingWrite.IsOfSameEntity).
    public bool KeysEqual(in EntityKey left, in EntityKey right) => KeyKind switch
    {
        KeyKindEnum.Integer => left.Integer == right.Integer,
        KeyKindEnum.Guid => left.Guid == right.Guid,
        _ => _entityType.Key.Declaration.TextComparison!.Equals(left.Text, right.Text),
    };

    // The integer key a root stores, as EntityKey.Integer holds it: read as the unsigned type it is
    // stored as when the key type is unsigned.
    public long StoredInteger(in RootReader stored) =>
        _unsignedKey ? unchecked((long)stored.GetUnsigned(_entityType.Key)) : stored.GetInteger(_entityType.Key);

    // The code units the text of a String key that a root stores takes at most: its bytes in a UTF-8
    // database, where every byte is at most one code unit, and half its bytes in a UTF-16 one; at least
    // 2 for a text that is not empty, the room a read of stored text needs for a surrogate pair
    // (StoredTextReader.Read).
    public int TextBound(in RootReader stored)
    {
        var bytes = stored.GetLength(_entityType.Key);

        if (bytes == 0)
        {
            return 0;
        }

        return Math.Max(2, _stringEncoding == LatticeStringEncodingEnum.Utf16 ? bytes / 2 : bytes);
    }

    // The hash of a key in the index, under the seed the instance drew when it opened, so that nobody who
    // does not know the seed can choose keys that share a hash and lengthen one chain of the index, and the
    // index depends on nothing a process sets. For a String, the seeded hash of the comparison its field
    // declares (Comparison.ValueComparer.TextHash), computed with the key. For an integer, held as a long,
    // and for a Guid, all its bits mixed by the finalizer of MurmurHash3 (Mix), the seed mixed in by an
    // exclusive or before the finalizer, into the integer and into the second half of the Guid:
    // Int64.GetHashCode and Guid.GetHashCode are the exclusive or of the halves or the quarters of the value,
    // which keys of a structure share systematically, such as (shard << 32) | id or (i << 32) | i, all of
    // hash 0. Under the seed 0 a key enters the finalizer unchanged. Keys that are one key (KeysEqual) have
    // one hash, so the coordinator of the writes compares the entities of two writes by it before it compares
    // their keys (Writes.PendingWrite.IsOfSameEntity).
    public uint HashOf(in EntityKey key)
    {
        if (KeyKind == KeyKindEnum.Text)
        {
            return key.TextHash;
        }

        if (KeyKind == KeyKindEnum.Integer)
        {
            return IntegerHash(key.Integer);
        }

        var guid = key.Guid;
        ref var halves = ref Unsafe.As<Guid, long>(ref guid);

        return GuidHash(halves, Unsafe.Add(ref halves, 1));
    }

    // The hash of an integer key in the index, of its 64 bits as EntityKey.Integer holds them (HashOf).
    public uint IntegerHash(long integer) => (uint)Mix((ulong)integer ^ _seed);

    // The hash of a Guid key in the index, of its two halves (HashOf).
    public uint GuidHash(long low, long high) => (uint)Mix((ulong)low ^ Mix((ulong)high ^ _seed));

    // The hash of a String key in the index, of the text a caller gave (HashOf); for Sets.KeyArgument,
    // which computes it before the read enters the protection of reads.
    public uint TextHash(ReadOnlySpan<char> text) => ValueComparer.TextHash(_entityType.Key, text, _seed);

    // The 64-bit finalizer fmix64 of MurmurHash3, by Austin Appleby, which he placed in the public
    // domain: every bit of the value changes every bit of the result with a probability near one half,
    // by its published constants. Two multiplications, a few nanoseconds.
    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCD;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53;
        value ^= value >> 33;

        return value;
    }

    // The kind of key a key field needs: the schema allows the eight integers, String and Guid.
    private static KeyKindEnum KeyKindOf(LatticeField key) => key.Declaration.Type switch
    {
        LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.Int16 or LatticeFieldTypeEnum.UInt16
            or LatticeFieldTypeEnum.Int32 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.Int64 or LatticeFieldTypeEnum.UInt64 => KeyKindEnum.Integer,
        LatticeFieldTypeEnum.Guid => KeyKindEnum.Guid,
        LatticeFieldTypeEnum.String => KeyKindEnum.Text,
        _ => throw new UnreachableException($"The schema declares the key {key.Name} of {key.Declaration.Type}, which no primary key is."),
    };

    private LatticeException InvalidKey(string reason) =>
        new(LatticeErrorEnum.ValidationFailed, $"A key of {_entityType.Name} received from the master is not valid: {reason}");
}
