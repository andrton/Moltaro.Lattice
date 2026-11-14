using System.Text;

namespace Moltaro.Lattice.Schema;

// Binary form of a compiled schema:
//   "MLSC", format version, schema version, entity type count,
//   per entity type: id, name, has-class flag [, class name], key field id,
//     field count, per field: id, name, type, nullable flag,
//     index count, per index: name, unique flag, field count, field ids.
internal static class SchemaBinaryFormat
{
    private const uint Magic = 0x43534C4D; // "MLSC" in little-endian byte order
    private const int FormatVersion = 3;
    private const int MaximumCount = 100_000;

    public static void Write(LatticeSchema schema, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(schema.Version);
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
                writer.Write((byte)field.Declaration.Type);
                writer.Write(field.Declaration.IsNullable);
            }

            writer.Write(entityType.Indexes.Count);
            foreach (var index in entityType.Indexes)
            {
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
            var entityTypes = new LatticeEntityType[ReadCount(reader)];
            for (var index = 0; index < entityTypes.Length; index++)
            {
                entityTypes[index] = ReadEntityType(reader);
            }

            return SchemaBuilder.BuildSchema(version, entityTypes);
        }
        catch (Exception exception) when (exception is EndOfStreamException or FormatException or DecoderFallbackException)
        {
            throw SchemaBuilder.Invalid("the compiled schema is truncated or damaged.", exception);
        }
    }

    private static LatticeEntityType ReadEntityType(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var name = reader.ReadString();
        var clrTypeName = reader.ReadBoolean() ? reader.ReadString() : null;
        var keyFieldId = reader.ReadInt32();

        var fields = new FieldDraft[ReadCount(reader)];
        for (var index = 0; index < fields.Length; index++)
        {
            fields[index] = new FieldDraft(reader.ReadInt32(), reader.ReadString(), (LatticeFieldTypeEnum)reader.ReadByte(), reader.ReadBoolean());
        }

        var indexes = new IndexDraft[ReadCount(reader)];
        for (var index = 0; index < indexes.Length; index++)
        {
            var indexName = reader.ReadString();
            var isUnique = reader.ReadBoolean();
            var fieldIds = new int[ReadCount(reader)];
            for (var part = 0; part < fieldIds.Length; part++)
            {
                fieldIds[part] = reader.ReadInt32();
            }

            indexes[index] = new IndexDraft(indexName, fieldIds, isUnique);
        }

        return SchemaBuilder.BuildEntityType(id, name, clrTypeName, fields, keyFieldId, indexes);
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        return count is >= 0 and <= MaximumCount
            ? count
            : throw SchemaBuilder.Invalid("the compiled schema is truncated or damaged.");
    }
}
