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
// descriptor is not part of a declaration, items are written sorted: enum types, owned types,
// entity types, fields and indexes by identity, which is unique in its scope, and the values
// of an enum type in ascending numeric order, each once however many names it has. The parts
// of an index keep their order, which is part of the index, and so do the steps of a path.
// Nothing is written in the order of a dictionary or a set.
//
// Every part delimits itself: a list starts with its count, and what follows a type byte
// depends on that type alone. A kind of declaration added to the model therefore leaves the
// encoding of a schema that does not use it as it is, when it is written as the payload of a
// new type byte, or as a section after the last one that is written only when it has content.
// The declarations of Epic 1, sprint 3 were added in that way: owned types as a section of
// their own, the other kinds as payloads of new type bytes, and the paths of index parts in
// the step count that was there before. A schema that declares none of them encodes as
// before, so its schema fingerprint is the same.
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
//                  per step, from the field of the entity type to the indexed field:
//                    uint32 field identity
//   only when the schema declares owned types:
//   uint32   owned type count; per owned type, by identity:
//              uint32 identity,
//              uint32 field count; per field, by identity: uint32 identity, declaration
//   declaration: byte type, byte nullable mark, then by type
//                  String:    byte comparison, byte string storage
//                  Enum:      uint32 identity of the enum type
//                  Owned:     uint32 identity of the owned type
//                  Reference: uint32 identity of the entity type it refers to
//                  List, Set: declaration of the element
//                  Map:       declaration of the key, declaration of the value
//                  any other: nothing
//
// The comparison of the key of an entity type that a reference refers to is covered by the
// declaration of that key, and the identity of that entity type by the Reference.
//
// Layout fingerprint of an entity type:
//   byte     'L' (0x4C)
//   byte     string encoding
//   uint32   identity of the primary key field
//   uint32   field count; per field, by identity: uint32 identity, layout declaration
//   only when the entity type holds owned values:
//   uint32   count of the owned types it holds, directly or inside other owned values; per
//            such owned type, by identity:
//              uint32 identity,
//              uint32 field count; per field, by identity: uint32 identity, layout declaration
//   layout declaration: byte type, byte nullable mark, then by type
//                  String:    byte string storage
//                  Enum:      byte underlying type of the enum type
//                  Owned:     uint32 identity of the owned type
//                  Reference: byte type of the key of the entity type it refers to
//                  List, Set: layout declaration of the element
//                  Map:       layout declaration of the key, layout declaration of the value
//                  any other: nothing
//
// Every owned type an entity type holds is encoded once, however many positions hold it, so
// the encoding grows with the declarations and not with the number of ways to reach them.
// Finding them ends because an owned type does not contain itself and each is visited once.
// The identity of the entity type is not part of its layout: layouts are compared for one
// entity type in two versions of a schema.
internal static class SchemaFingerprints
{
    private const byte SchemaDomain = (byte)'S';
    private const byte LayoutDomain = (byte)'L';

    public static LatticeFingerprint Schema(
        LatticeStringEncodingEnum stringEncoding,
        IReadOnlyList<LatticeEnumType> enumTypes,
        IReadOnlyList<LatticeOwnedType> ownedTypes,
        IReadOnlyList<LatticeEntityType> entityTypes)
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
            WriteFields(encoding, entityType.Fields, WriteDeclaration);

            encoding.Count(entityType.Indexes.Count);
            foreach (var index in entityType.Indexes.OrderBy(index => index.Id))
            {
                encoding.Identity(index.Id);
                encoding.Mark(index.IsUnique);

                encoding.Count(index.Parts.Count);
                foreach (var part in index.Parts)
                {
                    encoding.Count(part.Path.Count);
                    foreach (var step in part.Path)
                    {
                        encoding.Identity(step.Id);
                    }
                }
            }
        }

        if (ownedTypes.Count > 0)
        {
            WriteOwnedTypes(encoding, ownedTypes, WriteDeclaration);
        }

        return encoding.Digest();
    }

    public static LatticeFingerprint Layout(LatticeStringEncodingEnum stringEncoding, LatticeField key, IReadOnlyList<LatticeField> fields)
    {
        var encoding = new CanonicalWriter();
        encoding.Byte(LayoutDomain);
        encoding.Byte((byte)stringEncoding);
        encoding.Identity(key.Id);
        WriteFields(encoding, fields, WriteLayoutDeclaration);

        var held = HeldOwnedTypes(fields);
        if (held.Count > 0)
        {
            WriteOwnedTypes(encoding, held, WriteLayoutDeclaration);
        }

        return encoding.Digest();
    }

    private static void WriteFields(CanonicalWriter encoding, IReadOnlyList<LatticeField> fields, Action<CanonicalWriter, LatticeValueDeclaration> writeDeclaration)
    {
        encoding.Count(fields.Count);
        foreach (var field in fields.OrderBy(field => field.Id))
        {
            encoding.Identity(field.Id);
            writeDeclaration(encoding, field.Declaration);
        }
    }

    private static void WriteOwnedTypes(CanonicalWriter encoding, IReadOnlyCollection<LatticeOwnedType> ownedTypes, Action<CanonicalWriter, LatticeValueDeclaration> writeDeclaration)
    {
        encoding.Count(ownedTypes.Count);
        foreach (var ownedType in ownedTypes.OrderBy(ownedType => ownedType.Id))
        {
            encoding.Identity(ownedType.Id);
            WriteFields(encoding, ownedType.Fields, writeDeclaration);
        }
    }

    // The recursion follows the nesting of one declaration; an owned type is written by its
    // identity, and its fields in the section of owned types.
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

            case LatticeFieldTypeEnum.Owned:
                encoding.Identity(declaration.OwnedType!.Id);
                break;

            case LatticeFieldTypeEnum.Reference:
                encoding.Identity(declaration.Target!.EntityTypeId);
                break;

            case LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set:
                WriteDeclaration(encoding, declaration.Element!);
                break;

            case LatticeFieldTypeEnum.Map:
                WriteDeclaration(encoding, declaration.Key!);
                WriteDeclaration(encoding, declaration.Value!);
                break;
        }
    }

    // Only what decides how a value is stored: not the comparison, of an enum type only the
    // integer it is stored as, and of the target of a reference only the type of its key.
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

            case LatticeFieldTypeEnum.Owned:
                encoding.Identity(declaration.OwnedType!.Id);
                break;

            case LatticeFieldTypeEnum.Reference:
                encoding.Byte((byte)declaration.Target!.Key.Type);
                break;

            case LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set:
                WriteLayoutDeclaration(encoding, declaration.Element!);
                break;

            case LatticeFieldTypeEnum.Map:
                WriteLayoutDeclaration(encoding, declaration.Key!);
                WriteLayoutDeclaration(encoding, declaration.Value!);
                break;
        }
    }

    // The owned types whose values an entity of these fields stores: those its fields hold,
    // and those that the fields of an owned type held hold, each once. The walk keeps a list
    // of the owned types still to look into instead of recursing from one to the next.
    private static List<LatticeOwnedType> HeldOwnedTypes(IReadOnlyList<LatticeField> fields)
    {
        var held = new List<LatticeOwnedType>();
        var seen = new HashSet<LatticeOwnedType>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<LatticeOwnedType>();

        foreach (var field in fields)
        {
            Collect(field.Declaration);
        }

        while (pending.Count > 0)
        {
            foreach (var field in pending.Pop().Fields)
            {
                Collect(field.Declaration);
            }
        }

        return held;

        void Collect(LatticeValueDeclaration declaration)
        {
            switch (declaration.Type)
            {
                case LatticeFieldTypeEnum.Owned when seen.Add(declaration.OwnedType!):
                    held.Add(declaration.OwnedType!);
                    pending.Push(declaration.OwnedType!);
                    break;

                case LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set:
                    Collect(declaration.Element!);
                    break;

                case LatticeFieldTypeEnum.Map:
                    Collect(declaration.Key!);
                    Collect(declaration.Value!);
                    break;
            }
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
