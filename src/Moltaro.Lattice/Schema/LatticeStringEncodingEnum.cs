namespace Moltaro.Lattice.Schema;

/// <summary>
/// How the text of a database is encoded in storage. The names are the ones used in the
/// schema descriptor, as <c>StringEncoding</c>.
/// </summary>
/// <remarks>
/// One encoding serves the whole database, every field and the dictionary of shared text; it
/// is declared in the schema and is part of its fingerprint. The encoding decides memory use
/// and the cost of converting to and from a .NET <see cref="string"/>. It never changes the
/// text that is read back or how it compares: both encodings hold the same well-formed
/// Unicode text and give the same order.
/// </remarks>
public enum LatticeStringEncodingEnum : byte
{
    /// <summary>UTF-8: one byte for an ASCII character. The default.</summary>
    Utf8 = 1,

    /// <summary>
    /// UTF-16, the encoding of a .NET <see cref="string"/>: two bytes for an ASCII character,
    /// fewer than UTF-8 for common CJK text, and no transcoding when a value is read.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> A schema declares it, and is compiled, saved and loaded
    /// with it, but storing by it is not implemented: every write of any entity type of a
    /// schema that declares UTF-16 throws <see cref="NotSupportedException"/>.
    /// </remarks>
    Utf16 = 2,
}
