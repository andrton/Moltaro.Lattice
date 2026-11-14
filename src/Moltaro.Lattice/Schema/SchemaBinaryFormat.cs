using System.Text;

namespace Moltaro.Lattice.Schema;

// Binary form of a compiled schema, little-endian as BinaryWriter writes it:
//   "MLSC", format version, catalog version, string encoding (byte),
//   enum type count, per enum type: id, name, underlying type (byte), Flags mark,
//     member count, per member: name, value (low 64 bits, then high 64 bits), canonical mark,
//   entity type count, per entity type: id, name, has-class flag [, class name], key field id,
//     field count, per field: id, name, declaration,
//     index count, per index: id, name, unique flag, field count, field ids.
//   A declaration: type (byte), nullable flag, comparison (byte, 0 when the type has none),
//     string storage (byte, 0 when the type has none), has-enum flag [, enum type name].
//
// What is loaded is validated again by SchemaBuilder, as a descriptor is. Format 3, which
// came before index identities and enum types, is refused like any other unknown format:
// nothing was released, so such a schema is compiled again.
internal static class SchemaBinaryFormat
{
    private const uint Magic = 0x43534C4D; // "MLSC" in little-endian byte order
    private const int FormatVersion = 4;
    private const int MaximumCount = 100_000;

    public static void Write(LatticeSchema schema, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
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
                writer.Write(index.Fields.Count);
                foreach (var field in index.Fields)
                {
                    writer.Write(field.Id);
                }
            }
        }
    }

    public static LatticeSchema Read(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
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

            var enumTypes = new EnumTypeDraft[ReadCount(reader)];
            for (var index = 0; index < enumTypes.Length; index++)
            {
                enumTypes[index] = ReadEnumType(reader);
            }

            var entityTypes = new EntityTypeDraft[ReadCount(reader)];
            for (var index = 0; index < entityTypes.Length; index++)
            {
                entityTypes[index] = ReadEntityType(reader);
            }

            return SchemaBuilder.BuildSchema(version, stringEncoding, enumTypes, entityTypes);
        }
        catch (Exception exception) when (exception is EndOfStreamException or FormatException or DecoderFallbackException)
        {
            throw SchemaBuilder.Invalid("the compiled schema is truncated or damaged.", exception);
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
    }

    private static EnumTypeDraft ReadEnumType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = reader.ReadString();
        var underlyingType = (LatticeFieldTypeEnum)reader.ReadByte();
        var isFlags = reader.ReadBoolean();

        var members = new EnumMemberDraft[ReadCount(reader)];
        for (var index = 0; index < members.Length; index++)
        {
            var memberName = reader.ReadString();
            var lower = reader.ReadUInt64();
            var upper = reader.ReadUInt64();
            members[index] = new EnumMemberDraft(memberName, new Int128(upper, lower), reader.ReadBoolean());
        }

        return new EnumTypeDraft(id, name, underlyingType, isFlags, members);
    }

    private static EntityTypeDraft ReadEntityType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = reader.ReadString();
        var clrTypeName = reader.ReadBoolean() ? reader.ReadString() : null;
        var keyFieldId = reader.ReadInt32();

        var fields = new FieldDraft[ReadCount(reader)];
        for (var index = 0; index < fields.Length; index++)
        {
            fields[index] = new FieldDraft(reader.ReadInt32(), reader.ReadString(), ReadDeclaration(reader));
        }

        var indexes = new IndexDraft[ReadCount(reader)];
        for (var index = 0; index < indexes.Length; index++)
        {
            var indexId = reader.ReadInt32();
            var indexName = reader.ReadString();
            var isUnique = reader.ReadBoolean();
            var fieldIds = new int[ReadCount(reader)];
            for (var part = 0; part < fieldIds.Length; part++)
            {
                fieldIds[part] = reader.ReadInt32();
            }

            indexes[index] = new IndexDraft(indexId, indexName, fieldIds, isUnique);
        }

        return new EntityTypeDraft(id, name, clrTypeName, fields, keyFieldId, indexes);
    }

    private static ValueDeclarationDraft ReadDeclaration(BinaryReader reader)
    {
        var type = (LatticeFieldTypeEnum)reader.ReadByte();
        var isNullable = reader.ReadBoolean();
        var comparison = reader.ReadByte();
        var stringStorage = reader.ReadByte();
        var enumTypeName = reader.ReadBoolean() ? reader.ReadString() : null;

        return new ValueDeclarationDraft(
            type,
            isNullable,
            comparison == 0 ? null : (LatticeStringComparisonEnum)comparison,
            stringStorage == 0 ? null : (LatticeStringStorageEnum)stringStorage,
            enumTypeName);
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        return count is >= 0 and <= MaximumCount
            ? count
            : throw SchemaBuilder.Invalid("the compiled schema is truncated or damaged.");
    }
}
