using System.Text;

namespace Moltaro.Lattice.Schema;

// Binary form of a compiled schema, little-endian as BinaryWriter writes it:
//   "MLSC", format version, catalog version, string encoding (byte),
//   enum type count, per enum type: id, name, underlying type (byte), Flags mark,
//     member count, per member: name, value (low 64 bits, then high 64 bits), canonical mark,
//   entity type count, per entity type: id, name, has-class mark [, class name], key field id,
//     field count, per field: id, name, declaration,
//     index count, per index: id, name, unique mark, field count, field ids.
//   A declaration: type (byte), nullable mark, comparison (byte, 0 when the type has none),
//     string storage (byte, 0 when the type has none), has-enum mark [, enum type name].
//   A count and an id are 32-bit integers, a mark is one byte 0 or 1, a text is its length in
//   bytes as a 7-bit encoded integer followed by that many bytes of well-formed UTF-8.
//
// Format 3, which came before index identities and enum types, is refused like any other
// unknown format: nothing was released, so such a schema is compiled again.
//
// Loaded bytes may be damaged, and every damage ends in InvalidSchema. The reader is strict
// where it reads: a mark that is not 0 or 1, a negative count or length, text that is not
// well-formed UTF-8 and policies that do not fit the type are refused here; every other
// rule of the model is checked by SchemaBuilder, as for a descriptor. Nothing is allocated
// by a count or a length before the bytes it announces have been read, so a damaged count
// ends at the end of the data. What can fail while reading is known: the end of the data
// (EndOfStreamException), a 7-bit length of more than five bytes (FormatException) and text
// that is not UTF-8 (DecoderFallbackException), each turned into InvalidSchema. A failure of
// the stream itself is not damage and is not turned into one.
internal static class SchemaBinaryFormat
{
    private const uint Magic = 0x43534C4D; // "MLSC" in little-endian byte order
    private const int FormatVersion = 4;

    // Refuses bytes that are not UTF-8 instead of replacing them, so that a damaged name is not
    // loaded as another name. Immutable, so it is shared.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Write(LatticeSchema schema, Stream stream)
    {
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(schema.Version);
        writer.Write((byte)schema.StringEncoding);

        writer.Write(schema.EnumTypes.Count);
        foreach (var enumType in schema.EnumTypes)
        {
            writer.Write(enumType.Id);
            writer.Write(enumType.Name);
            writer.Write((byte)enumType.UnderlyingType);
            writer.Write(enumType.IsFlags);

            writer.Write(enumType.Members.Count);
            foreach (var member in enumType.Members)
            {
                writer.Write(member.Name);
                writer.Write(unchecked((ulong)member.Value));
                writer.Write(unchecked((ulong)(member.Value >> 64)));
                writer.Write(member.IsCanonical);
            }
        }

        writer.Write(schema.EntityTypes.Count);
        foreach (var entityType in schema.EntityTypes)
        {
            writer.Write(entityType.Id);
            writer.Write(entityType.Name);
            writer.Write(entityType.ClrTypeName is not null);
            if (entityType.ClrTypeName is not null)
            {
                writer.Write(entityType.ClrTypeName);
            }

            writer.Write(entityType.Key.Id);
            writer.Write(entityType.Fields.Count);
            foreach (var field in entityType.Fields)
            {
                writer.Write(field.Id);
                writer.Write(field.Name);
                WriteDeclaration(writer, field.Declaration);
            }

            writer.Write(entityType.Indexes.Count);
            foreach (var index in entityType.Indexes)
            {
                writer.Write(index.Id);
                writer.Write(index.Name);
                writer.Write(index.IsUnique);
                writer.Write(index.Parts.Count);
                foreach (var part in index.Parts)
                {
                    writer.Write(part.Field.Id);
                }
            }
        }
    }

    // Reads one compiled schema and leaves the stream right after it: what follows belongs to
    // the caller.
    public static LatticeSchema Read(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic)
            {
                throw SchemaBuilder.Invalid("the data is not a compiled schema.");
            }

            var formatVersion = reader.ReadInt32();
            if (formatVersion != FormatVersion)
            {
                throw SchemaBuilder.Invalid($"compiled schema format {formatVersion} is not supported; compile the schema again.");
            }

            var version = reader.ReadInt32();
            var stringEncoding = (LatticeStringEncodingEnum)reader.ReadByte();
            var enumTypes = ReadList(reader, ReadEnumType);
            var entityTypes = ReadList(reader, ReadEntityType);

            return SchemaBuilder.BuildSchema(version, stringEncoding, enumTypes, entityTypes);
        }
        catch (Exception exception) when (exception is EndOfStreamException or FormatException or DecoderFallbackException)
        {
            throw Damaged(exception);
        }
    }

    // A file holds one compiled schema and nothing after it, as Save(string) writes it; data
    // after the schema means the file is not what was saved.
    public static LatticeSchema ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var schema = Read(stream);
        if (stream.Position != stream.Length)
        {
            throw SchemaBuilder.Invalid("the file has data after the compiled schema.");
        }

        return schema;
    }

    private static void WriteDeclaration(BinaryWriter writer, LatticeValueDeclaration declaration)
    {
        writer.Write((byte)declaration.Type);
        writer.Write(declaration.IsNullable);
        writer.Write((byte)(declaration.Comparison ?? 0));
        writer.Write((byte)(declaration.StringStorage ?? 0));
        writer.Write(declaration.EnumType is not null);
        if (declaration.EnumType is not null)
        {
            writer.Write(declaration.EnumType.Name);
        }
    }

    private static EnumTypeDraft ReadEnumType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);
        var underlyingType = (LatticeFieldTypeEnum)reader.ReadByte();
        var isFlags = ReadMark(reader);
        var members = ReadList(reader, ReadEnumMember);

        return new EnumTypeDraft(id, name, underlyingType, isFlags, members);
    }

    private static EnumMemberDraft ReadEnumMember(BinaryReader reader)
    {
        var name = ReadText(reader);
        var lower = reader.ReadUInt64();
        var upper = reader.ReadUInt64();
        var isCanonical = ReadMark(reader);

        return new EnumMemberDraft(name, new Int128(upper, lower), isCanonical);
    }

    private static EntityTypeDraft ReadEntityType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);
        var clrTypeName = ReadMark(reader) ? ReadText(reader) : null;
        var keyFieldId = reader.ReadInt32();
        var fields = ReadList(reader, ReadField);
        var indexes = ReadList(reader, ReadIndex);

        return new EntityTypeDraft(id, name, clrTypeName, fields, keyFieldId, indexes);
    }

    private static FieldDraft ReadField(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);

        return new FieldDraft(id, name, ReadDeclaration(reader));
    }

    private static IndexDraft ReadIndex(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);
        var isUnique = ReadMark(reader);
        var fieldIds = ReadList(reader, fieldReader => fieldReader.ReadInt32());

        return new IndexDraft(id, name, fieldIds, isUnique);
    }

    // The writer gives a String both of its policies and every other type neither, so a
    // declaration that has them otherwise is damaged. Read as a descriptor, a missing policy
    // would quietly become its default.
    private static ValueDeclarationDraft ReadDeclaration(BinaryReader reader)
    {
        var type = (LatticeFieldTypeEnum)reader.ReadByte();
        var isNullable = ReadMark(reader);
        var comparison = reader.ReadByte();
        var stringStorage = reader.ReadByte();
        var enumTypeName = ReadMark(reader) ? ReadText(reader) : null;

        var hasPolicies = comparison != 0 && stringStorage != 0;
        var hasNoPolicy = comparison == 0 && stringStorage == 0;
        if (type == LatticeFieldTypeEnum.String ? !hasPolicies : !hasNoPolicy)
        {
            throw Damaged();
        }

        return new ValueDeclarationDraft(
            type,
            isNullable,
            comparison == 0 ? null : (LatticeStringComparisonEnum)comparison,
            stringStorage == 0 ? null : (LatticeStringStorageEnum)stringStorage,
            enumTypeName);
    }

    // The items are added as they are read, so a damaged count allocates nothing in advance.
    private static List<T> ReadList<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw Damaged();
        }

        var items = new List<T>();
        for (var index = 0; index < count; index++)
        {
            items.Add(read(reader));
        }

        return items;
    }

    private static bool ReadMark(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw Damaged(),
    };

    // A text as BinaryWriter writes it. BinaryReader.ReadString is not used: it reports a
    // negative length as an IOException, which is not damage of the stream.
    private static string ReadText(BinaryReader reader)
    {
        var length = reader.Read7BitEncodedInt();
        if (length < 0)
        {
            throw Damaged();
        }

        // Read in pieces, so that what is allocated grows with the bytes that are there and
        // not with a length that may be damaged.
        using var bytes = new MemoryStream();
        Span<byte> piece = stackalloc byte[256];
        var remaining = length;
        while (remaining > 0)
        {
            var part = piece[..Math.Min(remaining, piece.Length)];
            reader.BaseStream.ReadExactly(part);
            bytes.Write(part);
            remaining -= part.Length;
        }

        return StrictUtf8.GetString(bytes.GetBuffer(), 0, length);
    }

    private static LatticeException Damaged(Exception? innerException = null) =>
        SchemaBuilder.Invalid("the compiled schema is truncated or damaged.", innerException);
}
