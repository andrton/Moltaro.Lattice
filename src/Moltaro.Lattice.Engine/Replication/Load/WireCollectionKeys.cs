using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Keys in checked slices, for the uniqueness check without native placement. A packed group
// has no descriptors, and the key is read from the fixed places of its record. Other entries
// already name the bytes of just the key. Comparisons use the stored-value rules.
internal readonly ref struct WireCollectionKeys(
    StructureLayout element,
    LatticeStringEncodingEnum encoding,
    ulong seed,
    ReadOnlySpan<WireKey> positions,
    bool packed) : ICollectionKeySource
{
    private readonly ReadOnlySpan<WireKey> _positions = positions;

    private ref readonly StoredField Key => ref element.Place(0);

    private LatticeValueDeclaration Declaration => element.DeclarationOf(0);

    public static int PackedPerGroup(StructureLayout element) => ValueBlocks.ChunkPayload / element.Size;

    public uint Hash(int ordinal)
    {
        var key = At(ordinal);
        if (key.IsNull)
        {
            return CollectionKeys.NullHash(seed);
        }

        if (Key.Type == StoredTypeEnum.String)
        {
            var text = key.Bytes;
            return text.IsSingleSegment
                ? ValueComparer.TextHash(Declaration, new StoredTextReader(text.FirstSpan, encoding), seed)
                : ValueComparer.TextHash(Declaration, Decode(text).AsSpan(), seed);
        }

        Span<byte> bytes = stackalloc byte[Key.Width];
        key.Bytes.CopyTo(bytes);
        return ValueComparer.FixedHash(Key.Type, bytes, seed);
    }

    public bool Equal(int left, int right)
    {
        var first = At(left);
        var second = At(right);
        if (first.IsNull || second.IsNull)
        {
            return first.IsNull == second.IsNull;
        }

        if (Key.Type == StoredTypeEnum.String)
        {
            return TextEqual(first.Bytes, second.Bytes);
        }

        Span<byte> leftBytes = stackalloc byte[Key.Width];
        Span<byte> rightBytes = stackalloc byte[Key.Width];
        first.Bytes.CopyTo(leftBytes);
        second.Bytes.CopyTo(rightBytes);
        return ValueComparer.CompareStored(Key.Type, leftBytes, rightBytes) == 0;
    }

    private bool TextEqual(in ReadOnlySequence<byte> left, in ReadOnlySequence<byte> right)
    {
        if (left.IsSingleSegment)
        {
            var text = new StoredTextReader(left.FirstSpan, encoding);
            return right.IsSingleSegment
                ? ValueComparer.CompareText(text, new StoredTextReader(right.FirstSpan, encoding), Declaration.TextComparison!) == 0
                : ValueComparer.TextEquals(text, Declaration, Decode(right).AsSpan());
        }

        var leftText = Decode(left);
        return right.IsSingleSegment
            ? ValueComparer.TextEquals(new StoredTextReader(right.FirstSpan, encoding), Declaration, leftText.AsSpan())
            : Declaration.TextComparison!.Equals(leftText.AsSpan(), Decode(right).AsSpan());
    }

    // Like StoredTextReader, interpret UTF-8 with replacement and copy UTF-16 code units.
    // Transport segmentation can fall inside a code unit.
    private string Decode(in ReadOnlySequence<byte> bytes)
    {
        if (encoding == LatticeStringEncodingEnum.Utf8)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return string.Create((int)(bytes.Length / sizeof(char)), bytes, static (units, source) =>
        {
            var target = MemoryMarshal.AsBytes(units);
            source.Slice(0, target.Length).CopyTo(target);
        });
    }

    private WireKey At(int ordinal)
    {
        if (!packed)
        {
            return _positions[ordinal];
        }

        var perGroup = PackedPerGroup(element);
        var group = _positions[ordinal / perGroup].Bytes;
        var record = group.Slice((ordinal % perGroup) * element.Size, element.Size);
        return IsNull(Key, record)
            ? new WireKey(default, IsNull: true)
            : new WireKey(record.Slice(Key.Offset, Key.Width), IsNull: false);
    }

    public static bool IsNull(in StoredField key, in ReadOnlySequence<byte> record)
    {
        if (!key.IsNullable)
        {
            return false;
        }

        var reader = new SequenceReader<byte>(record.Slice(key.NullBit / 8));
        reader.TryRead(out var bits);
        return (bits & (1 << (key.NullBit % 8))) != 0;
    }
}
