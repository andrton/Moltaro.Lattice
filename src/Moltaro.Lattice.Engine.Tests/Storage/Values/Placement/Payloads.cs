namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// Values of a given length for the tests of placement: bytes and ASCII text in an order that does
// not repeat with the chunk payload, so that a chunk stored or read in the wrong place would show.
internal static class Payloads
{
    // Bytes of every value.
    public static byte[] Bytes(int length, int seed = 0)
    {
        var bytes = new byte[length];

        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31) + (index / 251) + seed);
        }

        return bytes;
    }

    // Printable ASCII text of this many characters: one byte each in UTF-8, two in UTF-16.
    public static string Ascii(int length, int seed = 0) =>
        string.Create(length, seed, static (characters, seed) =>
        {
            for (var index = 0; index < characters.Length; index++)
            {
                characters[index] = (char)(' ' + (((index * 31) + (index / 251) + seed) % 95));
            }
        });
}
