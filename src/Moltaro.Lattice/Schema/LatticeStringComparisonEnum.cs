namespace Moltaro.Lattice.Schema;

/// <summary>
/// How the values of a String field are compared: for equality and order in its indexes, its
/// unique indexes and, when it is the primary key, for finding an entity by key. The names
/// are the ones used in the schema descriptor, as <c>Comparison</c>.
/// </summary>
/// <remarks>
/// Neither comparison changes the stored text. A value is read back exactly as it was
/// written: its case, its whitespace and its Unicode normalization form are kept. The
/// comparison does not depend on the culture of the process.
/// </remarks>
public enum LatticeStringComparisonEnum : byte
{
    /// <summary>
    /// Exact comparison of the characters, as <see cref="StringComparison.Ordinal"/>:
    /// <c>Main</c> and <c>main</c> are different. The default.
    /// </summary>
    Ordinal = 1,

    /// <summary>
    /// Comparison of the characters that ignores case, as
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>: <c>Main</c> and <c>main</c> are equal,
    /// so a unique index refuses the second. It is not language-aware, and it does not
    /// remove accents, trim whitespace or normalize the text.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> A schema declares it, and is compiled, saved and loaded
    /// with it, but storing by it is not implemented. Until then no write of an entity type
    /// that has a field with this comparison is carried out: after the checks every write
    /// makes of its arguments and of the entity against the schema, it throws
    /// <see cref="NotSupportedException"/>.
    /// </remarks>
    OrdinalIgnoreCase = 2,
}
