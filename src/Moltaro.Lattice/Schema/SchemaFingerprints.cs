using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Moltaro.Lattice.Schema;

// The canonical encodings whose SHA-256 digests are the two fingerprints of a schema.
//
// An encoding depends on nothing but the declarations it covers. An integer has a fixed width
// and is written little-endian; a mark is one byte, 0 or 1; a type or a policy is the one-byte
// value of its public enum, whose values are fixed. No name, no text and no culture enters an
// encoding, and nothing of the process or the platform does. Where the order of the
// descriptor is not part of a declaration, items are written sorted: enum types, entity
// types, fields and indexes by identity, which is unique in its scope, and the values of an
// enum type in ascending numeric order, each once however many names it has. The fields of
// an index keep their order, which is part of the index.
//
// Every part delimits itself: a list starts with its count, and what follows a type byte
// depends on that type alone. A kind of declaration added to the model therefore leaves the
// encoding of a schema that does not use it as it is, when it is written as the payload of a
// new type byte, or as a section after the last one that is written only when it has content.
//
// Schema fingerprint:
//   byte     'S' (0x53), so that it is never the encoding of a layout
//   byte     string encoding
//   uint32   enum type count; per enum type, by identity:
//              uint32 identity, byte underlying type, byte Flags mark,
//              uint32 value count; per value, ascending: int128 value
//   uint32   entity type count; per entity type, by identity:
//              uint32 identity, uint32 identity of the primary key field,
//              uint32 field count; per field, by identity: uint32 identity, declaration
//              uint32 index count; per index, by identity:
//                uint32 identity, byte Unique mark,
//                uint32 part count; per part, in index order:
//                  uint32 step count (1: a part is a field of the entity type);
//                  per step: uint32 field identity
//   declaration: byte type, byte nullable mark, then by type
//                  String: byte comparison, byte string storage
//                  Enum:   uint32 identity of the enum type
//
// Layout fingerprint of an entity type:
//   byte     'L' (0x4C)
//   byte     string encoding
//   uint32   field count; per field, by identity: uint32 identity, layout declaration
//   layout declaration: byte type, byte nullable mark, then by type
//                  String: byte string storage
//                  Enum:   byte underlying type of the enum type
internal static class SchemaFingerprints
{
    private const byte SchemaDomain = (byte)'S';
    private const byte LayoutDomain = (byte)'L';

    public static LatticeFingerprint Schema(LatticeStringEncodingEnum stringEncoding, IReadOnlyList<LatticeEnumType> enumTypes, IReadOnlyList<LatticeEntityType> entityTypes)
    {
        var encoding = new CanonicalWriter();
        encoding.Byte(SchemaDomain);
        encoding.Byte((byte)stringEncoding);

        encoding.Count(enumTypes.Count);
        foreach (var enumType in enumTypes.OrderBy(enumType => enumType.Id))
        {
            encoding.Identity(enumType.Id);
            encoding.Byte((byte)enumType.UnderlyingType);
            encoding.Mark(enumType.IsFlags);

            encoding.Count(enumType.Values.Length);
            foreach (var value in enumType.Values)
            {
                encoding.Int128(value);
            }
        }

        encoding.Count(entityTypes.Count);
        foreach (var entityType in entityTypes.OrderBy(entityType => entityType.Id))
        {
            encoding.Identity(entityType.Id);
            encoding.Identity(entityType.Key.Id);

            encoding.Count(entityType.Fields.Count);
            foreach (var field in entityType.Fields.OrderBy(field => field.Id))
            {
                encoding.Identity(field.Id);
                WriteDeclaration(encoding, field.Declaration);
            }

            encoding.Count(entityType.Indexes.Count);
            foreach (var index in entityType.Indexes.OrderBy(index => index.Id))
            {
                encoding.Identity(index.Id);
                encoding.Mark(index.IsUnique);

                encoding.Count(index.Parts.Count);
                foreach (var part in index.Parts)
                {
                    encoding.Count(1);
                    encoding.Identity(part.Field.Id);
                }
            }
        }

        return encoding.Digest();
    }

    public static LatticeFingerprint Layout(LatticeStringEncodingEnum stringEncoding, IReadOnlyList<LatticeField> fields)
    {
        var encoding = new CanonicalWriter();
        encoding.Byte(LayoutDomain);
        encoding.Byte((byte)stringEncoding);

        encoding.Count(fields.Count);
        foreach (var field in fields.OrderBy(field => field.Id))
        {
            encoding.Identity(field.Id);
            WriteLayoutDeclaration(encoding, field.Declaration);
        }

        return encoding.Digest();
    }

    private static void WriteDeclaration(CanonicalWriter encoding, LatticeValueDeclaration declaration)
    {
        encoding.Byte((byte)declaration.Type);
        encoding.Mark(declaration.IsNullable);

        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.String:
                encoding.Byte((byte)declaration.Comparison!.Value);
                encoding.Byte((byte)declaration.StringStorage!.Value);
                break;

            case LatticeFieldTypeEnum.Enum:
                encoding.Identity(declaration.EnumType!.Id);
                break;
        }
    }

    // Only what decides how a value is stored: not the comparison, and of an enum type only
    // the integer it is stored as.
    private static void WriteLayoutDeclaration(CanonicalWriter encoding, LatticeValueDeclaration declaration)
    {
        encoding.Byte((byte)declaration.Type);
        encoding.Mark(declaration.IsNullable);

        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.String:
                encoding.Byte((byte)declaration.StringStorage!.Value);
                break;

            case LatticeFieldTypeEnum.Enum:
                encoding.Byte((byte)declaration.EnumType!.UnderlyingType);
                break;
        }
    }

    private sealed class CanonicalWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(256);

        public void Byte(byte value)
        {
            _buffer.GetSpan(1)[0] = value;
            _buffer.Advance(1);
        }

        public void Mark(bool value) => Byte(value ? (byte)1 : (byte)0);

        // Identities are at least 1 and counts are not negative, so both fit a UInt32.
        public void Identity(int value) => UInt32(checked((uint)value));

        public void Count(int value) => UInt32(checked((uint)value));

        public void Int128(Int128 value)
        {
            BinaryPrimitives.WriteInt128LittleEndian(_buffer.GetSpan(16), value);
            _buffer.Advance(16);
        }

        public LatticeFingerprint Digest()
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(_buffer.WrittenSpan, digest);
            return new LatticeFingerprint(digest);
        }

        private void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value);
            _buffer.Advance(4);
        }
    }
}
