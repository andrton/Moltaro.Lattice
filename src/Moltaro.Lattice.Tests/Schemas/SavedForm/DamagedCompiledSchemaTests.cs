using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static Moltaro.Lattice.Tests.Schemas.SavedForm.HandWrittenForm;

namespace Moltaro.Lattice.Tests.Schemas.SavedForm;

// A compiled schema whose bytes are damaged is refused with InvalidSchema, the code on which a
// caller compiles the schema again. No damage escapes as another exception, and text that is
// not UTF-8 is refused rather than read as other text. A file holds one compiled schema and
// nothing after it; a stream is left right after the schema.
public sealed class DamagedCompiledSchemaTests
{
    // Every part of the binary form: an ordinary and a Flags enum type with an alias, an
    // entity type with a class, both text policies, enum fields, a nullable Binary field and
    // two indexes, and an entity type without a class.
    private const string Json = """
        {
          "SchemaVersion": 2,
          "Enums": [
            { "Id": 1, "Name": "Plan", "UnderlyingType": "Int8", "Members": [ { "Name": "Free", "Value": -1, "Canonical": true }, { "Name": "Gratis", "Value": -1 } ] },
            { "Id": 2, "Name": "Rights", "UnderlyingType": "UInt64", "Flags": true, "Members": [ { "Name": "Read", "Value": 1 }, { "Name": "Admin", "Value": 9223372036854775808 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "Example.Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 2, "Name": "Region", "Type": "String", "StringStorage": "Dictionary", "Nullable": true },
                { "Id": 3, "Name": "Plan", "Type": "Enum", "Enum": "Plan" },
                { "Id": 4, "Name": "Rights", "Type": "Enum", "Enum": "Rights", "Nullable": true },
                { "Id": 5, "Name": "Photo", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByRegion", "Fields": [2, 3], "Unique": true }, { "Id": 2, "Name": "ByPlan", "Fields": [3] } ]
            },
            { "Id": 2, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" } ], "PrimaryKey": [1] }
          ]
        }
        """;

    [Fact]
    public void Text_with_a_negative_length_is_refused()
    {
        // The start of the body of a schema of the current format with one enum type: catalog
        // version 1, UTF-8, no owned types, one enum type with the identity 1, and then the
        // length of its name, -1 as a 7-bit encoded integer.
        byte[] data =
        [
            1, 0, 0, 0,
            1,
            0, 0, 0, 0,
            1, 0, 0, 0,
            1, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F,
        ];

        ExpectRefused(Form(data));
    }

    [Theory]
    // A continuation byte without a start, a byte that UTF-8 never has, a start byte without
    // its continuation.
    [InlineData(0x80)]
    [InlineData(0xFF)]
    [InlineData(0xC3)]
    public void Name_that_is_not_well_formed_UTF8_is_refused_rather_than_replaced(byte value)
    {
        var saved = Save();
        var at = saved.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Account"));
        Assert.True(at >= 0);

        saved[at + 3] = value;

        // The digest is written again for the changed name, so that the name is read.
        ExpectRefused(FitLengthAndDigest(saved));
    }

    [Fact]
    public void Hand_written_form_of_the_tests_below_is_the_form_that_Save_writes()
    {
        var schema = LatticeSchema.Compile("""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "N",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "Nullable": true } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        using var saved = new MemoryStream();
        schema.Save(saved);

        var written = Compiled(writer => Field(writer, 2, "Text", LatticeFieldTypeEnum.String, nullableMark: 1, comparison: 1, stringStorage: 1));

        Assert.Equal(saved.ToArray(), written);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void Mark_that_is_neither_0_nor_1_is_refused(byte mark)
    {
        // The nullable mark of a field that is nullable is 1. A reader that took any byte but 0
        // for 1 would load the damaged form as the same nullable field.
        var nullable = Load(Compiled(writer => Field(writer, 2, "Age", LatticeFieldTypeEnum.Int32, nullableMark: 1)));
        Assert.True(nullable.EntityTypes[0].Fields[1].Declaration.IsNullable);

        ExpectRefused(Compiled(writer => Field(writer, 2, "Age", LatticeFieldTypeEnum.Int32, nullableMark: mark)));
    }

    [Fact]
    public void Text_length_of_more_than_five_bytes_is_refused()
    {
        // A 7-bit encoded length has at most five bytes. The name of N has six continuation
        // bytes and an end.
        var data = Form(Bytes(writer =>
        {
            WriteHeader(writer);
            writer.Write(0); // enum types
            writer.Write(1); // entity types
            writer.Write(1); // identity of N
            writer.Write([0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01]);
            writer.Write("N"u8);
        }));

        ExpectRefused(data);
    }

    [Theory]
    // A String without one of its policies, or without both, which would otherwise become
    // their defaults; and a type that has no policy with one.
    [InlineData(LatticeFieldTypeEnum.String, 0, 1)]
    [InlineData(LatticeFieldTypeEnum.String, 1, 0)]
    [InlineData(LatticeFieldTypeEnum.String, 0, 0)]
    [InlineData(LatticeFieldTypeEnum.Int32, 1, 0)]
    [InlineData(LatticeFieldTypeEnum.Int32, 0, 1)]
    public void Declaration_whose_policies_do_not_fit_its_type_is_refused(LatticeFieldTypeEnum type, byte comparison, byte stringStorage)
    {
        // A String with both policies, and an Int32 with neither, load.
        Load(Compiled(writer => Field(writer, 2, "Text", LatticeFieldTypeEnum.String, comparison: 2, stringStorage: 2)));
        Load(Compiled(writer => Field(writer, 2, "Text", LatticeFieldTypeEnum.Int32)));

        ExpectRefused(Compiled(writer => Field(writer, 2, "Text", type, comparison: comparison, stringStorage: stringStorage)));
    }

    [Theory]
    // The count of enum types, which comes first, and the count of indexes, which comes last.
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(int.MinValue, 0)]
    public void Negative_count_is_refused(int enumTypeCount, int indexCount)
    {
        // The same form with empty lists loads; a reader that took a negative count for zero
        // would load the damaged form as it.
        Load(Compiled());

        ExpectRefused(Compiled(enumTypeCount: enumTypeCount, indexCount: indexCount));
    }

    [Theory]
    // Two gigabytes and one gigabyte of text, announced by a name that has three bytes.
    [InlineData(int.MaxValue)]
    [InlineData(1 << 30)]
    public void Text_length_far_beyond_the_data_costs_memory_for_the_data_only(int declaredLength)
    {
        var data = Form(Bytes(writer =>
        {
            WriteHeader(writer);
            writer.Write(0); // enum types
            writer.Write(1); // entity types
            writer.Write(1); // identity of N
            writer.Write7BitEncodedInt(declaredLength);
            writer.Write("abc"u8);
        }));

        ExpectRefusedAllocatingLittle(data);
    }

    [Theory]
    // More enum types than an array can hold, and enough for a gigabyte of references,
    // announced before one enum type without members.
    [InlineData(int.MaxValue)]
    [InlineData(1 << 27)]
    public void Count_far_beyond_the_data_costs_memory_for_the_data_only(int declaredCount)
    {
        var data = Form(Bytes(writer =>
        {
            WriteHeader(writer);
            writer.Write(declaredCount); // enum types
            writer.Write(1); // identity
            writer.Write("E");
            writer.Write((byte)LatticeFieldTypeEnum.Int32);
            writer.Write((byte)0); // not Flags
            writer.Write(0); // members
        }));

        ExpectRefusedAllocatingLittle(data);
    }

    [Fact]
    public void Every_truncation_of_a_saved_schema_is_refused()
    {
        var saved = Save();

        for (var length = 0; length < saved.Length; length++)
        {
            ExpectRefused(saved[..length]);
        }
    }

    [Fact]
    public void File_with_data_after_the_compiled_schema_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-{Guid.NewGuid():N}.schema");
        try
        {
            File.WriteAllBytes(path, [.. Save(), 0]);

            TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stream_is_left_right_after_the_compiled_schema()
    {
        var saved = Save();
        using var stream = new MemoryStream([.. saved, 42]);

        LatticeSchema.Load(stream);

        // What follows the schema belongs to the caller.
        Assert.Equal(saved.Length, stream.Position);
        Assert.Equal(42, stream.ReadByte());
    }

    [Fact]
    public void Form_made_from_the_body_of_a_saved_schema_is_the_saved_schema()
    {
        // What the helpers below write is the form that Save writes.
        var saved = Save();

        Assert.Equal(saved, Form(saved[12..^32]));
        Assert.Equal(saved, FitLengthAndDigest(saved));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Negative_length_of_the_body_is_refused(int length)
    {
        // The same form with its own length loads.
        var form = FitLengthAndDigest(Save());
        Load(form);

        BinaryPrimitives.WriteInt32LittleEndian(form.AsSpan(8), length);

        ExpectRefused(form);
    }

    [Theory]
    // Two gigabytes and one gigabyte of body, announced before the few hundred bytes there are.
    [InlineData(int.MaxValue)]
    [InlineData(1 << 30)]
    public void Length_of_the_body_far_beyond_the_data_costs_memory_for_the_data_only(int length)
    {
        var form = FitLengthAndDigest(Save());
        Load(form);

        BinaryPrimitives.WriteInt32LittleEndian(form.AsSpan(8), length);

        ExpectRefusedAllocatingLittle(form);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void Byte_added_at_the_end_of_the_body_is_refused_also_with_its_length_and_digest_fitted(byte added)
    {
        var saved = Save();
        Load(FitLengthAndDigest(saved));

        // The digest is right for the longer body, which holds a byte after the schema.
        ExpectRefused(FitLengthAndDigest([.. saved[..^32], added, .. saved[^32..]]));
    }

    [Fact]
    public void Last_byte_of_the_body_taken_away_is_refused_also_with_its_length_and_digest_fitted()
    {
        var saved = Save();
        Load(FitLengthAndDigest(saved));

        // The digest is right for the shorter body, which ends before the schema does.
        ExpectRefused(FitLengthAndDigest([.. saved[..^33], .. saved[^32..]]));
    }

    [Theory]
    // The format of Epic 1, sprint 2, and one that does not exist yet.
    [InlineData(4)]
    [InlineData(6)]
    public void Form_of_another_format_version_is_refused_also_with_its_digest_fitted(int formatVersion)
    {
        var form = FitLengthAndDigest(Save());
        Load(form);

        BinaryPrimitives.WriteInt32LittleEndian(form.AsSpan(4), formatVersion);
        SHA256.HashData(form.AsSpan(..^32)).CopyTo(form.AsSpan(^32));

        ExpectRefused(form);
    }

    [Fact]
    public void Form_with_another_magic_is_refused_also_with_its_digest_fitted()
    {
        var form = FitLengthAndDigest(Save());
        Load(form);

        "MLSD"u8.CopyTo(form);
        SHA256.HashData(form.AsSpan(..^32)).CopyTo(form.AsSpan(^32));

        ExpectRefused(form);
    }

    [Fact]
    public void Change_that_still_reads_as_a_schema_is_refused()
    {
        // Without a digest, Account would load as an entity type Bccount.
        var renamed = Save();
        var at = renamed.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Account"));
        Assert.True(at >= 0);

        renamed[at] = (byte)'B';

        ExpectRefused(renamed);
    }

    [Fact]
    public void Every_single_byte_change_of_a_saved_schema_is_refused()
    {
        var saved = Save();
        var changed = new byte[saved.Length];

        // The magic, the format version, the length, every byte of the body and of the digest.
        for (var position = 0; position < saved.Length; position++)
        {
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                if (value == saved[position])
                {
                    continue;
                }

                saved.CopyTo(changed, 0);
                changed[position] = (byte)value;

                var exception = Record.Exception(() => LatticeSchema.Load(new MemoryStream(changed)));
                if (exception is not LatticeException { Error: LatticeErrorEnum.InvalidSchema })
                {
                    Assert.Fail($"Byte {position} changed to {value}: {exception?.ToString() ?? "loaded"}");
                }
            }
        }
    }

    [Fact]
    public void Owned_type_that_contains_itself_is_refused_on_load_with_the_owned_types_on_the_way_named()
    {
        // Save cannot write such a schema, so its body is written by hand. With a field Child
        // of type Int32 the owned type Node loads; with Child of type Node it contains itself.
        var harmless = Load(Form(SelfContainingBody(writer =>
        {
            writer.Write((byte)LatticeFieldTypeEnum.Int32);
            writer.Write((byte)1); // nullable
            writer.Write((byte)0); // no comparison
            writer.Write((byte)0); // no string storage
            writer.Write((byte)0); // no enum type
        })));
        Assert.True(harmless.TryGetOwnedType("Node", out _));

        var data = Form(SelfContainingBody(writer =>
        {
            writer.Write((byte)LatticeFieldTypeEnum.Owned);
            writer.Write((byte)1); // nullable
            writer.Write((byte)0); // no comparison
            writer.Write((byte)0); // no string storage
            writer.Write((byte)0); // no enum type
            writer.Write("Node"); // the owned type
        }));

        var exception = Assert.Throws<LatticeException>(() => Load(data));
        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Node", exception.Message);
    }

    private static byte[] Save()
    {
        using var stream = new MemoryStream();
        LatticeSchema.Compile(Json).Save(stream);
        return stream.ToArray();
    }

    private static LatticeSchema Load(byte[] data) => LatticeSchema.Load(new MemoryStream(data));

    private static void ExpectRefused(byte[] data) =>
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => Load(data));

    // Refused, and the load allocates no more than a fixed bound, generous for the few bytes
    // that are there and far below what the declared count or length would need. The load
    // runs on this thread, so the bytes allocated by this thread measure it on any machine.
    private static void ExpectRefusedAllocatingLittle(byte[] data)
    {
        const long Bound = 1 << 20;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Record.Exception(() => Load(data));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var refused = Assert.IsType<LatticeException>(exception);
        Assert.Equal(LatticeErrorEnum.InvalidSchema, refused.Error);
        Assert.InRange(allocated, 0, Bound);
    }

    // The start of a body: catalog version 1, UTF-8, no owned types.
    private static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write((byte)LatticeStringEncodingEnum.Utf8);
        writer.Write(0); // owned types
    }

    // The whole form of a schema with one entity type N, whose key field Id is an Int64,
    // followed by the field that writeField writes. It has no enum type and no index unless a
    // count says otherwise, with nothing written for what such a count announces.
    private static byte[] Compiled(Action<BinaryWriter>? writeField = null, int enumTypeCount = 0, int indexCount = 0) => Form(Bytes(writer =>
    {
        WriteHeader(writer);
        writer.Write(enumTypeCount);
        writer.Write(1); // entity types
        writer.Write(1); // identity of N
        writer.Write("N");
        writer.Write((byte)0); // no class
        writer.Write(1); // key field
        writer.Write(writeField is null ? 1 : 2);
        Field(writer, 1, "Id", LatticeFieldTypeEnum.Int64);
        writeField?.Invoke(writer);
        writer.Write(indexCount);
    }));

    // The body of a schema with one owned type Node, whose one field Child has the declaration
    // that writeChild writes, and one entity type Tree with an Int64 key and nothing else.
    private static byte[] SelfContainingBody(Action<BinaryWriter> writeChild) => Bytes(writer =>
    {
        writer.Write(1); // catalog version
        writer.Write((byte)LatticeStringEncodingEnum.Utf8);
        writer.Write(1); // owned types
        writer.Write(1); // identity of Node
        writer.Write("Node");
        writer.Write((byte)0); // no class
        writer.Write(1); // fields
        writer.Write(1); // identity of Child
        writer.Write("Child");
        writeChild(writer);
        writer.Write(0); // enum types
        writer.Write(1); // entity types
        writer.Write(1); // identity of Tree
        writer.Write("Tree");
        writer.Write((byte)0); // no class
        writer.Write(1); // key field
        writer.Write(1); // fields
        Field(writer, 1, "Id", LatticeFieldTypeEnum.Int64);
        writer.Write(0); // indexes
    });

    // A declaration without an enum type; a policy is the value of its public enum, 0 for none.
    private static void Field(BinaryWriter writer, int id, string name, LatticeFieldTypeEnum type, byte nullableMark = 0, byte comparison = 0, byte stringStorage = 0)
    {
        writer.Write(id);
        writer.Write(name);
        writer.Write((byte)type);
        writer.Write(nullableMark);
        writer.Write(comparison);
        writer.Write(stringStorage);
        writer.Write((byte)0); // no enum type
    }
}
