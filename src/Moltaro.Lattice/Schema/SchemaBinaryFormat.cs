using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Moltaro.Lattice.Schema;

// Binary form of a compiled schema, format 5. The frame is fixed from outside, every integer
// of it little-endian:
//   "MLSC", format version (Int32), length of the body (Int32, not negative), the body, and a
//   digest: 32 bytes of SHA-256 over every byte before it, from the magic to the last byte of
//   the body.
//
// The body is this class's own, as BinaryWriter writes it:
//   catalog version, string encoding (byte),
//   owned type count, per owned type: id, name, has-class mark [, class name],
//     field count, per field: id, name, declaration,
//   enum type count, per enum type: id, name, underlying type (byte), Flags mark,
//     member count, per member: name, value (low 64 bits, then high 64 bits), canonical mark,
//   entity type count, per entity type: id, name, has-class mark [, class name], key field id,
//     field count, per field: id, name, declaration,
//     index count, per index: id, name, unique mark, part count,
//       per part: step count, the field ids of the steps from the field of the entity type.
//   A declaration: type (byte), nullable mark, comparison (byte, 0 when the type has none),
//     string storage (byte, 0 when the type has none), has-enum mark [, enum type name], and
//     after that by type: Owned the owned type name, Reference the entity type name, List and
//     Set the declaration of the element, Map the declarations of the key and of the value;
//     any other type nothing.
//   A count and an id are 32-bit integers, a mark is one byte 0 or 1, a text is its length in
//   bytes as a 7-bit encoded integer followed by that many bytes of well-formed UTF-8.
//
// Format 4, which had no digest, and every earlier format are refused like any other unknown
// format: nothing was released, so such a schema is compiled again.
//
// Loaded bytes may be damaged, and every damage ends in InvalidSchema. Load goes through the
// frame in its order and refuses at the first rule that does not hold:
//   1. The magic and the format version.
//   2. The length. A negative one is refused. The length counts the bytes of the body to read
//      and is used for nothing else: the body is read in pieces, so memory grows with the
//      bytes that arrived and not with the length, and data that ends before the body and
//      the digest are complete is refused.
//   3. The digest, computed over the bytes that were read and compared with the 32 bytes
//      after them. Nothing of the body is decoded before it agreed; the magic, the format
//      version and the length are the only values taken from the data before, and the digest
//      covers them too.
//   4. The body. A digest shows damage, not who wrote the bytes, so the body is still read
//      strictly: a mark that is not 0 or 1, a negative count or length, text that is not
//      well-formed UTF-8, policies that do not fit the type and a declaration nested deeper
//      than any descriptor can declare are refused here. The schema fills the body exactly:
//      bytes left after it, and a schema that needs more bytes than the body holds, are
//      refused. Every rule of the model is checked by SchemaBuilder, as for a descriptor.
// Nothing is allocated by a count or a length before the bytes it announces have been read, so
// a damaged count ends at the end of the body. What can fail while reading is known: the end
// of the data (EndOfStreamException), a 7-bit length of more than five bytes
// (FormatException) and text that is not UTF-8 (DecoderFallbackException), each turned into
// InvalidSchema. A failure of the stream itself is not damage and is not turned into one.
internal static class SchemaBinaryFormat
{
    private const uint Magic = 0x43534C4D; // "MLSC" in little-endian byte order
    private const int FormatVersion = 5;

    // Magic, format version and length of the body.
    private const int HeaderLength = 12;
    private const int DigestLength = 32;

    // The body is read in pieces of this size.
    private const int PieceLength = 1024;

    // A declaration nests at most this deep in a body. The JSON reader stops at a depth of 64,
    // the default of JsonDocument, so no descriptor declares a deeper one and no schema that
    // Save writes holds one. Refusing a deeper declaration keeps the recursion of reading
    // bounded by this number instead of by the data.
    private const int MaximumNesting = 64;

    // Refuses bytes that are not UTF-8 instead of replacing them, so that a damaged name is not
    // loaded as another name. Immutable, so it is shared.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Write(LatticeSchema schema, Stream stream)
    {
        using var form = new MemoryStream();
        using (var writer = new BinaryWriter(form, StrictUtf8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(0); // the length of the body, written below once it is known
            WriteBody(writer, schema);
        }

        var written = form.GetBuffer().AsSpan(0, checked((int)form.Length));
        BinaryPrimitives.WriteInt32LittleEndian(written[8..], written.Length - HeaderLength);

        Span<byte> digest = stackalloc byte[DigestLength];
        SHA256.HashData(written, digest);

        stream.Write(written);
        stream.Write(digest);
    }

    // Reads one compiled schema and leaves the stream right after its digest: what follows
    // belongs to the caller.
    public static LatticeSchema Read(Stream stream)
    {
        try
        {
            using var form = ReadVerifiedForm(stream);
            using var reader = new BinaryReader(form, StrictUtf8, leaveOpen: true);
            form.Position = HeaderLength;

            var version = reader.ReadInt32();
            var stringEncoding = (LatticeStringEncodingEnum)reader.ReadByte();
            var ownedTypes = ReadList(reader, ReadOwnedType);
            var enumTypes = ReadList(reader, ReadEnumType);
            var entityTypes = ReadList(reader, ReadEntityType);

            if (form.Position != form.Length)
            {
                throw SchemaBuilder.Invalid("the body of the compiled schema has bytes after the schema.");
            }

            return SchemaBuilder.BuildSchema(version, stringEncoding, enumTypes, ownedTypes, entityTypes);
        }
        catch (Exception exception) when (exception is EndOfStreamException or FormatException or DecoderFallbackException)
        {
            throw Damaged(exception);
        }
    }

    // A file holds one compiled schema and nothing after it, as Save(string) writes it; data
    // after the digest means the file is not what was saved.
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

    // Steps 1 to 3 of Load: the header, the body and the digest. Returns the header and the
    // body, whose digest agreed, without having decoded anything of the body.
    private static MemoryStream ReadVerifiedForm(Stream stream)
    {
        Span<byte> piece = stackalloc byte[PieceLength];
        var header = piece[..HeaderLength];
        stream.ReadExactly(header);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw SchemaBuilder.Invalid("the data is not a compiled schema.");
        }

        var formatVersion = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (formatVersion != FormatVersion)
        {
            throw SchemaBuilder.Invalid($"compiled schema format {formatVersion} is not supported; compile the schema again.");
        }

        // Save writes the whole form into one array first, so no saved body is longer than an
        // array can hold after the header: a longer length is damage, as a negative one is.
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (length < 0 || length > Array.MaxLength - HeaderLength)
        {
            throw Damaged();
        }

        // The form grows with what is read, never by the length it announces.
        var form = new MemoryStream();
        form.Write(header);
        for (var remaining = length; remaining > 0;)
        {
            var part = piece[..Math.Min(remaining, piece.Length)];
            stream.ReadExactly(part);
            form.Write(part);
            remaining -= part.Length;
        }

        Span<byte> digest = stackalloc byte[DigestLength];
        stream.ReadExactly(digest);

        Span<byte> computed = stackalloc byte[DigestLength];
        SHA256.HashData(form.GetBuffer().AsSpan(0, checked((int)form.Length)), computed);
        if (!computed.SequenceEqual(digest))
        {
            throw SchemaBuilder.Invalid("the digest of the compiled schema does not agree with its bytes: the data is damaged.");
        }

        return form;
    }

    private static void WriteBody(BinaryWriter writer, LatticeSchema schema)
    {
        writer.Write(schema.Version);
        writer.Write((byte)schema.StringEncoding);

        writer.Write(schema.OwnedTypes.Count);
        foreach (var ownedType in schema.OwnedTypes)
        {
            writer.Write(ownedType.Id);
            writer.Write(ownedType.Name);
            WriteClass(writer, ownedType.ClrTypeName);
            WriteFields(writer, ownedType.Fields);
        }

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
            WriteClass(writer, entityType.ClrTypeName);
            writer.Write(entityType.Key.Id);
            WriteFields(writer, entityType.Fields);

            writer.Write(entityType.Indexes.Count);
            foreach (var index in entityType.Indexes)
            {
                writer.Write(index.Id);
                writer.Write(index.Name);
                writer.Write(index.IsUnique);
                writer.Write(index.Parts.Count);
                foreach (var part in index.Parts)
                {
                    writer.Write(part.Path.Count);
                    foreach (var step in part.Path)
                    {
                        writer.Write(step.Id);
                    }
                }
            }
        }
    }

    private static void WriteClass(BinaryWriter writer, string? clrTypeName)
    {
        writer.Write(clrTypeName is not null);
        if (clrTypeName is not null)
        {
            writer.Write(clrTypeName);
        }
    }

    private static void WriteFields(BinaryWriter writer, IReadOnlyList<LatticeField> fields)
    {
        writer.Write(fields.Count);
        foreach (var field in fields)
        {
            writer.Write(field.Id);
            writer.Write(field.Name);
            WriteDeclaration(writer, field.Declaration);
        }
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

        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Owned:
                writer.Write(declaration.OwnedType!.Name);
                break;

            case LatticeFieldTypeEnum.Reference:
                writer.Write(declaration.ReferencedEntityTypeName!);
                break;

            case LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set:
                WriteDeclaration(writer, declaration.Element!);
                break;

            case LatticeFieldTypeEnum.Map:
                WriteDeclaration(writer, declaration.Key!);
                WriteDeclaration(writer, declaration.Value!);
                break;
        }
    }

    private static OwnedTypeDraft ReadOwnedType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);
        var clrTypeName = ReadMark(reader) ? ReadText(reader) : null;
        var fields = ReadList(reader, ReadField);

        return new OwnedTypeDraft(id, name, clrTypeName, fields);
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

        return new FieldDraft(id, name, ReadDeclaration(reader, nesting: 1));
    }

    private static IndexDraft ReadIndex(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = ReadText(reader);
        var isUnique = ReadMark(reader);
        var parts = ReadList(reader, partReader => ReadList(partReader, stepReader => stepReader.ReadInt32()));

        return new IndexDraft(id, name, parts, isUnique);
    }

    // The writer gives a String both of its policies and every other type neither, so a
    // declaration that has them otherwise is damaged. Read as a descriptor, a missing policy
    // would quietly become its default. What follows depends on the type alone.
    private static ValueDeclarationDraft ReadDeclaration(BinaryReader reader, int nesting)
    {
        if (nesting > MaximumNesting)
        {
            throw Damaged();
        }

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

        var ownedTypeName = type == LatticeFieldTypeEnum.Owned ? ReadText(reader) : null;
        var entityTypeName = type == LatticeFieldTypeEnum.Reference ? ReadText(reader) : null;
        var element = type is LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set ? ReadDeclaration(reader, nesting + 1) : null;
        var key = type == LatticeFieldTypeEnum.Map ? ReadDeclaration(reader, nesting + 1) : null;
        var value = type == LatticeFieldTypeEnum.Map ? ReadDeclaration(reader, nesting + 1) : null;

        return new ValueDeclarationDraft(
            type,
            isNullable,
            comparison == 0 ? null : (LatticeStringComparisonEnum)comparison,
            stringStorage == 0 ? null : (LatticeStringStorageEnum)stringStorage,
            enumTypeName,
            ownedTypeName,
            entityTypeName,
            element,
            key,
            value);
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
