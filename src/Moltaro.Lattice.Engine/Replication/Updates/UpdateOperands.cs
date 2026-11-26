using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Updates;

// The logical operands of both requests and committed effects, independent
// of the sender's placement and lookup form.
internal static class UpdateOperands
{
    public static long Length(StructureLayout layout, int position, object? value)
    {
        var declaration = layout.DeclarationOf(position);
        var length = declaration.IsNullable ? 1L : 0;
        if (value is null)
        {
            return length;
        }

        if (declaration.Type == LatticeFieldTypeEnum.Owned)
        {
            var owned = (LatticeOwnedValue)value;
            foreach (var field in declaration.OwnedType!.FieldsByOrdinal)
            {
                length += Length(layout, layout.ChildOf(position, field), owned.Values[field.Ordinal]);
            }

            return length;
        }

        if (declaration.Map is { } map)
        {
            var entry = layout.ElementOf(position);
            length += sizeof(uint);
            foreach (var (key, item) in map.Entries(value))
            {
                length += Length(entry, 0, key) + Length(entry, 1, item);
            }

            return length;
        }

        if (declaration.Sequence is { } sequence)
        {
            var entry = layout.ElementOf(position);
            length += sizeof(uint);
            foreach (var item in sequence.Elements(value))
            {
                length += Length(entry, 0, item);
            }

            return length;
        }

        return length + (value switch
        {
            string text => (long)sizeof(uint) + Encoding.UTF8.GetByteCount(text),
            byte[] bytes => sizeof(uint) + (long)bytes.Length,
            _ => layout.Place(position).Width,
        });
    }

    public static void Write(IBufferWriter<byte> output, StructureLayout layout, int position, object? value)
    {
        var declaration = layout.DeclarationOf(position);
        if (declaration.IsNullable)
        {
            output.GetSpan(1)[0] = value is null ? (byte)0 : (byte)1;
            output.Advance(1);
        }

        if (value is null)
        {
            return;
        }

        if (declaration.Type == LatticeFieldTypeEnum.Owned)
        {
            var owned = (LatticeOwnedValue)value;
            foreach (var field in declaration.OwnedType!.FieldsByOrdinal)
            {
                Write(output, layout, layout.ChildOf(position, field), owned.Values[field.Ordinal]);
            }
        }
        else if (declaration.Map is { } map)
        {
            var entry = layout.ElementOf(position);
            WriteUInt32(output, (uint)entry.CountEntries(value));
            foreach (var (key, item) in map.Entries(value))
            {
                Write(output, entry, 0, key);
                Write(output, entry, 1, item);
            }
        }
        else if (declaration.Sequence is { } sequence)
        {
            var entry = layout.ElementOf(position);
            WriteUInt32(output, (uint)sequence.Count(value));
            foreach (var item in sequence.Elements(value))
            {
                Write(output, entry, 0, item);
            }
        }
        else if (value is string text)
        {
            WriteUInt32(output, (uint)Encoding.UTF8.GetByteCount(text));
            var left = text.AsSpan();
            while (!left.IsEmpty)
            {
                Utf8.FromUtf16(left, output.GetSpan(4), out var read, out var written);
                output.Advance(written);
                left = left[read..];
            }
        }
        else if (value is byte[] bytes)
        {
            WriteUInt32(output, (uint)bytes.Length);
            WriteBytes(output, bytes);
        }
        else
        {
            var width = layout.Place(position).Width;
            var bytesOfScalar = output.GetSpan(width)[..width];
            StoredScalars.WriteBoxed(bytesOfScalar, value);
            NativeOrder(bytesOfScalar, declaration.ScalarType);
            output.Advance(width);
        }
    }

    public static object? Read(ref SequenceReader<byte> input, LatticeEntityType type, StructureLayout layout, int position,
        MemoryLease? lease = null)
    {
        var declaration = layout.DeclarationOf(position);
        if (declaration.IsNullable)
        {
            if (!input.TryRead(out var present) || present > 1)
            {
                throw Protocol.Invalid("An update operand has an invalid presence marker.");
            }

            if (present == 0)
            {
                return null;
            }
        }

        if (declaration.Type == LatticeFieldTypeEnum.Owned)
        {
            var owned = LatticeOwnedValue.Snapshot(declaration.OwnedType!, lease);
            foreach (var member in owned.OwnedType.FieldsByOrdinal)
            {
                owned.Values[member.Ordinal] = Read(ref input, type, layout, layout.ChildOf(position, member), lease);
            }

            return owned;
        }

        if (declaration.Sequence is not null || declaration.Map is not null)
        {
            return ReadCollection(ref input, type, layout.ElementOf(position), declaration, lease);
        }

        ref readonly var field = ref layout.Place(position);
        object value;
        if (declaration.ScalarType == LatticeFieldTypeEnum.String)
        {
            // A UTF-8 representation of a stored UTF-16 value can take
            // three bytes per code unit. Check the schema's own bound below.
            var bound = type.StringEncoding == LatticeStringEncodingEnum.Utf16
                ? (uint)(ValueBlocks.MostValueBytes / 2 * 3)
                : ValueBlocks.MostValueBytes;
            var bytes = ReadBytes(ref input, bound);
            try
            {
                value = ReadText(bytes, lease);
            }
            catch (DecoderFallbackException)
            {
                throw Protocol.Invalid("An update operand holds malformed UTF-8.");
            }
        }
        else if (declaration.Type == LatticeFieldTypeEnum.Binary)
        {
            var bytes = ReadBytes(ref input, ValueBlocks.MostValueBytes);
            if (lease is null)
            {
                value = bytes.ToArray();
            }
            else
            {
                var copy = lease.Allocate<byte>((int)bytes.Length, nameof(UpdateOperands));
                bytes.CopyTo(copy);
                value = copy;
            }
        }
        else
        {
            Span<byte> scalar = stackalloc byte[16];
            var bytes = scalar[..field.Width];
            if (!input.TryCopyTo(bytes) || (declaration.ScalarType == LatticeFieldTypeEnum.Boolean && bytes[0] > 1))
            {
                throw Protocol.Invalid("An update operand has invalid fixed-width bytes.");
            }

            input.Advance(bytes.Length);
            NativeOrder(bytes, declaration.ScalarType);
            try
            {
                value = ReadScalar(bytes, declaration.ScalarType, lease);
            }
            catch (ArgumentException)
            {
                throw Protocol.Invalid("An update operand has a scalar outside its declared type.");
            }
        }

        RecordValues.CheckCopiedLeaf(declaration, value, type, field.Field);
        return value;
    }

    private static string ReadText(ReadOnlySequence<byte> bytes, MemoryLease? lease)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (lease is null && bytes.IsSingleSegment)
        {
            return Protocol.StrictUtf8.GetString(bytes.FirstSpan);
        }

        var characters = bytes.IsSingleSegment ? Protocol.StrictUtf8.GetCharCount(bytes.FirstSpan) : DecodeSplitText(bytes, []);
        var capacity = MemoryBudget.StringBytes(characters);
        lease?.Budget.Reserve(capacity);
        try
        {
            // Writing directly into the exact string needs only the bytes of one split rune
            // on the stack. The sequence GetString would rent full-sized scratch per piece.
            var text = bytes.IsSingleSegment
                ? Protocol.StrictUtf8.GetString(bytes.FirstSpan)
                : string.Create(characters, bytes, static (output, source) => DecodeSplitText(source, output));
            lease?.OwnReserved(text, capacity, nameof(UpdateOperands));
            return text;
        }
        catch
        {
            lease?.Budget.Release(capacity);
            throw;
        }
    }

    // An empty destination counts UTF-16 units. Both passes use the same strict decoder.
    private static int DecodeSplitText(in ReadOnlySequence<byte> bytes, Span<char> output)
    {
        var reader = new SequenceReader<byte>(bytes);
        Span<char> scratch = stackalloc char[256];
        Span<byte> split = stackalloc byte[4];
        bool countsOnly = output.IsEmpty;
        var characters = 0;
        while (!reader.End)
        {
            var piece = reader.UnreadSpan;
            var destination = countsOnly ? scratch : output[characters..];
            var status = Utf8.ToUtf16(piece, destination, out var read, out var written,
                replaceInvalidSequences: false, isFinalBlock: reader.Remaining == piece.Length);
            reader.Advance(read);
            characters += written;
            if (status == OperationStatus.NeedMoreData)
            {
                var available = split[..(int)Math.Min(reader.Remaining, split.Length)];
                if (!reader.TryCopyTo(available) || Rune.DecodeFromUtf8(available, out var rune, out var consumed) != OperationStatus.Done)
                {
                    throw new DecoderFallbackException();
                }

                characters += countsOnly ? rune.Utf16SequenceLength : rune.EncodeToUtf16(output[characters..]);
                reader.Advance(consumed);
            }
            else if (status == OperationStatus.InvalidData)
            {
                throw new DecoderFallbackException();
            }
            else if (status == OperationStatus.DestinationTooSmall && !countsOnly)
            {
                throw new InvalidOperationException("A counted UTF-8 operand did not fit its string.");
            }
        }

        return characters;
    }

    private static object ReadScalar(ReadOnlySpan<byte> bytes, LatticeFieldTypeEnum scalar, MemoryLease? lease)
    {
        if (lease is null)
        {
            return StoredScalars.ReadBoxed(bytes, scalar);
        }

        if (scalar == LatticeFieldTypeEnum.DateTimeOffset)
        {
            // Its stored instant is eight bytes, while the boxed .NET value also holds an offset.
            return lease.Box(StoredScalars.ReadDateTimeOffset(bytes), nameof(UpdateOperands));
        }

        var capacity = MemoryBudget.ObjectBytes(bytes.Length);
        lease.Budget.Reserve(capacity);
        try
        {
            var value = StoredScalars.ReadBoxed(bytes, scalar);
            lease.OwnReserved(value, capacity, nameof(UpdateOperands));
            return value;
        }
        catch
        {
            lease.Budget.Release(capacity);
            throw;
        }
    }

    private static object ReadCollection(ref SequenceReader<byte> input, LatticeEntityType type, StructureLayout entry,
        LatticeValueDeclaration declaration, MemoryLease? lease)
    {
        var count = ReadUInt32(ref input);
        var smallest = Minimum(entry, 0);
        if (declaration.Map is { } map)
        {
            smallest += Minimum(entry, 1);
            CheckCount(count, entry.MostElements, smallest, input.Remaining);
            var result = map.Create(lease: lease);
            for (var index = 0u; index < count; index++)
            {
                var key = Read(ref input, type, entry, 0, lease)!;
                var item = Read(ref input, type, entry, 1, lease);
                if (!map.TryAdd(result, key, item, lease))
                {
                    throw Protocol.Invalid("An update operand repeats a Map key under its declared comparison.");
                }
            }

            return result;
        }

        CheckCount(count, entry.MostElements, smallest, input.Remaining);
        var sequence = declaration.Sequence!;
        var collection = sequence.Create(lease: lease);
        for (var index = 0u; index < count; index++)
        {
            sequence.Add(collection, Read(ref input, type, entry, 0, lease), lease);
        }

        if (sequence.Count(collection) != count)
        {
            throw Protocol.Invalid("An update operand repeats a Set member under its declared comparison.");
        }

        return collection;
    }

    private static int Minimum(StructureLayout layout, int position)
    {
        var declaration = layout.DeclarationOf(position);
        if (declaration.IsNullable)
        {
            return 1;
        }

        if (declaration.Type == LatticeFieldTypeEnum.Owned)
        {
            var length = 0;
            foreach (var field in declaration.OwnedType!.FieldsByOrdinal)
            {
                length += Minimum(layout, layout.ChildOf(position, field));
            }

            return length;
        }

        return declaration.Type is LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Binary ||
            declaration.ScalarType == LatticeFieldTypeEnum.String ? sizeof(uint) : layout.Place(position).Width;
    }

    public static void CheckCount(uint count, int maximum, int minimumSize, long remaining)
    {
        if (count > (uint)maximum || count > remaining / minimumSize)
        {
            throw Protocol.Invalid("An update body announces a count beyond its bound or remaining bytes.");
        }
    }

    public static uint ReadUInt32(ref SequenceReader<byte> input)
    {
        if (!input.TryReadLittleEndian(out int value))
        {
            throw Protocol.Invalid("An update body ends before a four-byte value.");
        }

        return unchecked((uint)value);
    }

    public static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(sizeof(uint)), value);
        output.Advance(sizeof(uint));
    }

    public static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var place = output.GetSpan(1);
            var count = Math.Min(place.Length, bytes.Length);
            bytes[..count].CopyTo(place);
            output.Advance(count);
            bytes = bytes[count..];
        }
    }

    private static ReadOnlySequence<byte> ReadBytes(ref SequenceReader<byte> input, uint maximum)
    {
        var length = ReadUInt32(ref input);
        if (length > maximum || length > input.Remaining)
        {
            throw Protocol.Invalid("An update operand announces bytes beyond its bound or remaining body.");
        }

        var bytes = input.UnreadSequence.Slice(0, length);
        input.Advance(length);
        return bytes;
    }

    // The wire is little-endian. A Guid already has its defined byte order,
    // and Decimal is four separate Int32 values in the scalar representation.
    private static void NativeOrder(Span<byte> bytes, LatticeFieldTypeEnum scalar)
    {
        if (BitConverter.IsLittleEndian || scalar == LatticeFieldTypeEnum.Guid)
        {
            return;
        }

        var part = scalar == LatticeFieldTypeEnum.Decimal ? sizeof(int) : bytes.Length;
        for (var offset = 0; offset < bytes.Length; offset += part)
        {
            bytes.Slice(offset, part).Reverse();
        }
    }
}
