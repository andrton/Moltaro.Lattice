using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Fixture;

// The SHA-256 of the values of records, by which a run shows that it generated the same fixture as
// every other. Every record is encoded in a form of its own that names everything it holds and
// depends on nothing of the machine or the runtime, and the encodings are hashed in the order of the
// records:
// - the name of the entity type, then every field in the order the entity type declares them, each
//   as its name and its value;
// - a value as one byte that tells its kind and then its bytes: null; a Boolean as one byte, 0 or 1;
//   an Int64, which a reference of the fixture is too, as eight bytes, the lowest first; a String as
//   the length of its UTF-8 and those bytes; a List as the count of its elements and each element
//   in its order; a Set and a Map as their count and their members each encoded alone, a member of a
//   Map as its key and its value, the members in the order of their encodings, so that the order in
//   which a Set or a Map enumerates changes nothing;
// - a length or a count as four bytes, the lowest first.
// The fixture holds no value of another type, and such a value is refused with ArgumentException.
internal static class RecordDigest
{
    private const byte NullKind = 0;

    private const byte BooleanKind = 1;

    private const byte Int64Kind = 2;

    private const byte StringKind = 3;

    private const byte ListKind = 4;

    private const byte SetKind = 5;

    private const byte MapKind = 6;

    // The digest as lowercase hexadecimal.
    public static string Of(IEnumerable<LatticeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoding = new ArrayBufferWriter<byte>();

        foreach (var record in records)
        {
            encoding.ResetWrittenCount();
            WriteText(encoding, record.EntityType.Name);

            foreach (var field in record.EntityType.Fields)
            {
                WriteText(encoding, field.Name);
                WriteValue(encoding, field, record[field.Name]);
            }

            hash.AppendData(encoding.WrittenSpan);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void WriteValue(ArrayBufferWriter<byte> encoding, LatticeField field, object? value)
    {
        switch (value)
        {
            case null:
                WriteByte(encoding, NullKind);
                break;
            case bool boolean:
                WriteByte(encoding, BooleanKind);
                WriteByte(encoding, boolean ? (byte)1 : (byte)0);
                break;
            case long integer:
                WriteByte(encoding, Int64Kind);
                BinaryPrimitives.WriteInt64LittleEndian(encoding.GetSpan(sizeof(long)), integer);
                encoding.Advance(sizeof(long));
                break;
            case string text:
                WriteByte(encoding, StringKind);
                WriteText(encoding, text);
                break;
            case IDictionary map:
                var entries = new List<byte[]>(map.Count);

                // The enumerator of IDictionary gives the entries; that of IEnumerable gives them in
                // the form of the dictionary's own type.
                foreach (DictionaryEntry entry in map)
                {
                    entries.Add(Encoded(field, entry.Key, entry.Value));
                }

                WriteByte(encoding, MapKind);
                WriteMembers(encoding, entries);
                break;
            case IList list:
                WriteByte(encoding, ListKind);
                WriteCount(encoding, list.Count);

                foreach (var element in list)
                {
                    WriteValue(encoding, field, element);
                }

                break;
            case IEnumerable set when field.Declaration.Type == LatticeFieldTypeEnum.Set:
                WriteByte(encoding, SetKind);
                WriteMembers(encoding, [.. set.Cast<object?>().Select(member => Encoded(field, member))]);
                break;
            default:
                throw new ArgumentException($"The field {field.Name} holds a {value.GetType().Name}, which is no value of the reference fixture.", nameof(field));
        }
    }

    // The members of a Set or a Map, in the order of their encodings.
    private static void WriteMembers(ArrayBufferWriter<byte> encoding, List<byte[]> members)
    {
        WriteCount(encoding, members.Count);

        foreach (var member in members.Order(Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right))))
        {
            encoding.Write(member);
        }
    }

    // The values of one member, encoded on their own.
    private static byte[] Encoded(LatticeField field, params object?[] values)
    {
        var member = new ArrayBufferWriter<byte>();

        foreach (var value in values)
        {
            WriteValue(member, field, value);
        }

        return member.WrittenSpan.ToArray();
    }

    private static void WriteText(ArrayBufferWriter<byte> encoding, string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);

        WriteCount(encoding, length);
        Encoding.UTF8.GetBytes(text, encoding.GetSpan(length));
        encoding.Advance(length);
    }

    private static void WriteCount(ArrayBufferWriter<byte> encoding, int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(encoding.GetSpan(sizeof(int)), count);
        encoding.Advance(sizeof(int));
    }

    private static void WriteByte(ArrayBufferWriter<byte> encoding, byte value)
    {
        encoding.GetSpan(1)[0] = value;
        encoding.Advance(1);
    }
}
