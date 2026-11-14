using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Moltaro.Lattice.Tests;

// The binary form of a compiled schema, as far as it is fixed from outside: the four bytes
// MLSC, the format version 5 and the length of the body, each an Int32 in little-endian order,
// then the body, then 32 bytes of digest, SHA-256 over every byte before it. The encoding of
// the body is the format's own. A form of another format, that of Epic 1, sprint 2 included,
// is not loaded: nothing was released, so such a schema is compiled again.
public sealed class CompiledSchemaFormTests
{
    private const int HeaderLength = 12;
    private const int DigestLength = 32;

    // A schema saved in the form of Epic 1, sprint 2, format 4, which has no digest, compiled
    // from this descriptor:
    //   { "EntityTypes": [ { "Id": 1, "Name": "Note",
    //     "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
    //     "PrimaryKey": [1], "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ] } ] }
    private const string SavedInTheFormOfSprint2 = "TUxTQwQAAAABAAAAAQAAAAABAAAAAQAAAAROb3RlAAEAAAACAAAAAQAAAAJJZAgAAAAAAgAAAARUZXh0DQABAQABAAAAAQAAAAZCeVRleHQAAQAAAAIAAAA=";

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Saved_schema_is_the_magic_the_format_version_the_length_of_the_body_the_body_and_its_digest()
    {
        var saved = Save(ValueSchema.Compile());

        Assert.Equal("MLSC"u8.ToArray(), saved[..4]);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(4)));

        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(8));
        Assert.InRange(bodyLength, 1, int.MaxValue);
        Assert.Equal(HeaderLength + bodyLength + DigestLength, saved.Length);

        // The digest covers everything before it, from the magic to the last byte of the body.
        Assert.Equal(SHA256.HashData(saved.AsSpan(..^DigestLength)), saved[^DigestLength..]);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Stream_is_left_right_after_the_digest()
    {
        var saved = Save(ValueSchema.Compile());
        using var stream = new MemoryStream([.. saved, .. saved, 42]);

        // Two schemas one after the other: each load ends where the length of its body and
        // the digest say.
        var first = LatticeSchema.Load(stream);
        var end = HeaderLength + BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(8)) + DigestLength;
        Assert.Equal(end, stream.Position);

        var second = LatticeSchema.Load(stream);
        Assert.Equal(2 * end, stream.Position);
        Assert.Equal(42, stream.ReadByte());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void File_with_data_after_the_digest_is_refused()
    {
        var saved = Save(ValueSchema.Compile());
        var path = Path.Combine(Path.GetTempPath(), $"lattice-{Guid.NewGuid():N}.schema");
        try
        {
            // The file as saved loads; with a byte after the digest, or a second schema, it is
            // not the file that was saved.
            File.WriteAllBytes(path, saved);
            LatticeSchema.Load(path);

            File.WriteAllBytes(path, [.. saved, 0]);
            TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(path));

            File.WriteAllBytes(path, [.. saved, .. saved]);
            TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Schema_saved_in_the_form_of_sprint_2_does_not_load()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(SavedInTheFormOfSprint2));

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(stream));
    }

    private static byte[] Save(LatticeSchema schema)
    {
        using var stream = new MemoryStream();
        schema.Save(stream);
        return stream.ToArray();
    }
}
