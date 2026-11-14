namespace Moltaro.Lattice.Schema;

/// <summary>
/// How the text of a String field is stored. The names are the ones used in the schema
/// descriptor, as <c>StringStorage</c>.
/// </summary>
/// <remarks>
/// The choice decides memory use and the work of a write. It never changes the text that is
/// read back or how the field compares, see <see cref="LatticeStringComparisonEnum"/>.
/// </remarks>
public enum LatticeStringStorageEnum : byte
{
    /// <summary>
    /// Every value is stored on its own: the same text in two entities is stored twice. The
    /// default; suited to text that rarely repeats.
    /// </summary>
    Plain = 1,

    /// <summary>
    /// The same text is stored once for the whole database and shared by every field that
    /// selects Dictionary, also across entity types; suited to text that repeats, such as a
    /// region or a category. Exactly equal text is shared: on an
    /// <see cref="LatticeStringComparisonEnum.OrdinalIgnoreCase"/> field <c>North</c> and
    /// <c>north</c> are still stored as two spellings.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> A schema declares it, and is compiled, saved and loaded
    /// with it, but storing by it is not implemented: every write of an entity type that has
    /// a field with this storage throws <see cref="NotSupportedException"/>.
    /// </remarks>
    Dictionary = 2,
}
