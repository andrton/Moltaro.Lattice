using System.Security.Cryptography;
using System.Text;

namespace Moltaro.Lattice.Tests.Schemas.SavedForm;

// A saved form of a compiled schema written by hand, for the tests that need a form Save does
// not write: one with a part written wrong, or one that declares what no descriptor can.
internal static class HandWrittenForm
{
    // Bytes of the binary form, written by hand part by part as the reader expects them, so
    // that a test can write one part wrong and every other part as Save writes it: a body,
    // which Form makes a whole form, or in Form the bytes before the digest.
    public static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    // The form of Epic 1, sprint 3, around the bytes of a body: the magic, format version 5,
    // the length of the body, the body, and the digest, SHA-256 over every byte before it.
    public static byte[] Form(byte[] body)
    {
        var beforeDigest = Bytes(writer =>
        {
            writer.Write("MLSC"u8);
            writer.Write(5);
            writer.Write(body.Length);
            writer.Write(body);
        });

        return [.. beforeDigest, .. SHA256.HashData(beforeDigest)];
    }

    // Writes the length and the digest of a form of Epic 1, sprint 3 again, after a test changed
    // its body: the body is what lies between the 12 bytes of magic, format version and length,
    // and the 32 bytes of the digest.
    public static byte[] FitLengthAndDigest(byte[] form) => Form(form[12..^32]);

    // The part of a declaration that every type has: the type, not nullable, no text policies
    // and no enum type. A List is followed by the declaration of its element.
    public static void WriteDeclaration(BinaryWriter writer, LatticeFieldTypeEnum type)
    {
        writer.Write((byte)type);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
    }
}
