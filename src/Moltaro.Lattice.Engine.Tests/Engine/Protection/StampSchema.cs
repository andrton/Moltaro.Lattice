using System.Buffers.Binary;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The schema of the tests of the protection of reads, and the classes of its two entity types: Stamp,
// with the key Id, an Int64, and NamedStamp, with the key Name, a String of 100 characters that lies
// in a block of its own, so that the index compares a key with text that a replace or a delete frees.
// Both have the same fields besides the key, in every place a value can lie (brief of S05, "Where a
// value goes"): Number, an Int64 in the fixed part; Label, a String inline in the root block; Text, a
// String of 300 characters in a block of its own; Data, a Binary of 5,008 bytes in a chunk.
//
// A test names a key by a small number and a state of it by another, and every value of the state is
// derived from the two. A read that mixes two states, that reads memory freed or given to another
// root, or that returns the entity of another key has values that are not those of one state of the
// key it asked for, and NumberOf fails it.
internal static class StampSchema
{
    public const int NameLength = 100;

    public const int TextLength = 300;

    // 313 groups of 16 bytes, each the key and the number of the state: more than the 4,096 bytes of
    // a block of its own, so the value lies in a chunk.
    public const int DataLength = 313 * 16;

    // The two entity types.
    public static LatticeSchema Schema { get; } = Compile(StampType, NamedStampType);

    // Stamp alone, for a test that counts the primary index of one entity type: PrimaryIndexBytes adds
    // up the indexes of every entity type of the schema.
    public static LatticeSchema StampsOnly { get; } = Compile(StampType);

    private static string StampType => $$"""
        {
          "Id": 1, "Name": "Stamp", "ClrType": "{{typeof(Stamp).FullName}}",
          "Fields": [
            { "Id": 1, "Name": "Id", "Type": "Int64" },
            { "Id": 2, "Name": "Number", "Type": "Int64" },
            { "Id": 3, "Name": "Label", "Type": "String" },
            { "Id": 4, "Name": "Text", "Type": "String" },
            { "Id": 5, "Name": "Data", "Type": "Binary" }
          ],
          "PrimaryKey": [1]
        }
        """;

    private static string NamedStampType => $$"""
        {
          "Id": 2, "Name": "NamedStamp", "ClrType": "{{typeof(NamedStamp).FullName}}",
          "Fields": [
            { "Id": 1, "Name": "Name", "Type": "String" },
            { "Id": 2, "Name": "Number", "Type": "Int64" },
            { "Id": 3, "Name": "Label", "Type": "String" },
            { "Id": 4, "Name": "Text", "Type": "String" },
            { "Id": 5, "Name": "Data", "Type": "Binary" }
          ],
          "PrimaryKey": [1]
        }
        """;

    public static Task<EngineDatabase> OpenAsync() => EngineMaster.OpenAsync(Schema);

    // The entity type whose key has the type a test names: Int64 or String.
    public static string EntityTypeOf(string keyType) => keyType switch
    {
        "Int64" => "Stamp",
        "String" => "NamedStamp",
        _ => throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "No entity type has such a key."),
    };

    // The primary key of the key number in the entity type: the number for Stamp, a name for NamedStamp.
    public static object KeyOf(LatticeEntityType entityType, int key) =>
        entityType.Name == "Stamp" ? (long)key : NameOf(key);

    public static string NameOf(int key) =>
        string.Create(CultureInfo.InvariantCulture, $"stamp {key:D4} ").PadRight(NameLength, '.');

    // The state with the number of the key, as a record.
    public static LatticeRecord RecordOf(LatticeEntityType entityType, int key, long number) => new(entityType)
    {
        [entityType.Key.Name] = KeyOf(entityType, key),
        ["Number"] = number,
        ["Label"] = LabelOf(key, number),
        ["Text"] = TextOf(key, number),
        ["Data"] = DataOf(key, number),
    };

    // The same state as an object of Stamp.
    public static Stamp StampOf(int key, long number) => new()
    {
        Id = key,
        Number = number,
        Label = LabelOf(key, number),
        Text = TextOf(key, number),
        Data = DataOf(key, number),
    };

    // The number of the state a read returned for the key, after checking that every value read is of
    // that state of that key; null when the read found no entity.
    public static long? NumberOf(int key, Stamp? stamp) =>
        stamp is null ? null : Checked(key, (long)key, stamp.Id, stamp.Number, stamp.Label, stamp.Text, stamp.Data);

    public static long? NumberOf(int key, NamedStamp? stamp) =>
        stamp is null ? null : Checked(key, NameOf(key), stamp.Name, stamp.Number, stamp.Label, stamp.Text, stamp.Data);

    public static long? NumberOf(int key, LatticeRecord? record) =>
        record is null
            ? null
            : Checked(
                key,
                KeyOf(record.EntityType, key),
                record[record.EntityType.Key.Name],
                record.Get<long>("Number"),
                record.Get<string>("Label"),
                record.Get<string>("Text"),
                record.Get<byte[]>("Data"));

    private static LatticeSchema Compile(params string[] entityTypes) =>
        LatticeSchema.Compile($$"""{ "EntityTypes": [ {{string.Join(", ", entityTypes)}} ] }""");

    private static long Checked(int key, object expectedKey, object? storedKey, long number, string? label, string? text, byte[]? data)
    {
        Assert.Equal(expectedKey, storedKey);
        Assert.Equal(LabelOf(key, number), label);
        Assert.True(text is not null && IsTextOf(text, key, number), $"The Text read for key {key} is not that of its state {number}.");
        Assert.True(data is not null && IsDataOf(data, key, number), $"The Data read for key {key} is not that of its state {number}.");

        return number;
    }

    private static string LabelOf(int key, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"{key}/{number}");

    private static string TokenOf(int key, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"{key:D4}:{number:D10}|");

    private static string TextOf(int key, long number) =>
        string.Create(TextLength, TokenOf(key, number), static (span, token) =>
        {
            for (var start = 0; start < span.Length; start += token.Length)
            {
                token.AsSpan(0, Math.Min(token.Length, span.Length - start)).CopyTo(span[start..]);
            }
        });

    private static byte[] DataOf(int key, long number)
    {
        var data = new byte[DataLength];

        for (var start = 0; start < DataLength; start += 16)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(start), key);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(start + 8), number);
        }

        return data;
    }

    // Whether the text is the token of the state repeated to its length, compared without a copy.
    private static bool IsTextOf(string text, int key, long number)
    {
        var token = TokenOf(key, number);

        if (text.Length != TextLength)
        {
            return false;
        }

        for (var start = 0; start < TextLength; start += token.Length)
        {
            var part = text.AsSpan(start, Math.Min(token.Length, TextLength - start));

            if (!part.SequenceEqual(token.AsSpan(0, part.Length)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDataOf(byte[] data, int key, long number)
    {
        if (data.Length != DataLength)
        {
            return false;
        }

        for (var start = 0; start < DataLength; start += 16)
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(start)) != key
                || BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(start + 8)) != number)
            {
                return false;
            }
        }

        return true;
    }

    public sealed class Stamp
    {
        public long Id { get; set; }

        public long Number { get; set; }

        public string Label { get; set; } = "";

        public string Text { get; set; } = "";

        public byte[] Data { get; set; } = [];
    }

    public sealed class NamedStamp
    {
        public string Name { get; set; } = "";

        public long Number { get; set; }

        public string Label { get; set; } = "";

        public string Text { get; set; } = "";

        public byte[] Data { get; set; } = [];
    }
}
