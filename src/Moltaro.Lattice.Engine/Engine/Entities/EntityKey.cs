using System.Globalization;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Entities;

// A primary key in the form the index of its entity type takes it (KeyKindEnum), made by
// Sets.KeyArgument from what a caller gave after it checked it against the key type the schema
// declares. A value, so that a lookup passes it without boxing:
// - an integer key holds its value as the 64 bits of Integer: the value itself for a signed key type,
//   the value zero-extended for UInt8 to UInt32, and the bits of the UInt64 for a UInt64 key, so a
//   key above the largest Int64 is a negative Integer. Within one key type that is one Integer for each
//   value and one value for each Integer, which is all an index needs; the index of a key type of at
//   most 32 bits takes the low 32 bits;
// - a Guid key holds the Guid;
// - a String key holds the text and its hash under the comparison the key field declares, computed
//   when the key is made, before any gate is taken (Comparison.ValueComparer.TextHash).
// The members of the other kinds are empty.
internal readonly struct EntityKey
{
    private readonly LatticeFieldTypeEnum _type;

    private EntityKey(LatticeFieldTypeEnum type, long integer, Guid guid, string? text, int textHash)
    {
        _type = type;
        Integer = integer;
        Guid = guid;
        Text = text;
        TextHash = textHash;
    }

    public long Integer { get; }

    public Guid Guid { get; }

    public string? Text { get; }

    public int TextHash { get; }

    // An integer key of the key type, given as its 64 bits as Integer says.
    public static EntityKey OfInteger(LatticeFieldTypeEnum type, long bits) => new(type, bits, default, null, 0);

    public static EntityKey OfGuid(Guid guid) => new(LatticeFieldTypeEnum.Guid, 0, guid, null, 0);

    public static EntityKey OfText(string text, int hash) => new(LatticeFieldTypeEnum.String, 0, default, text, hash);

    // The key for a message, as the caller gave it.
    public override string ToString() => _type switch
    {
        LatticeFieldTypeEnum.String => Text!,
        LatticeFieldTypeEnum.Guid => Guid.ToString(),
        LatticeFieldTypeEnum.UInt64 => unchecked((ulong)Integer).ToString(CultureInfo.InvariantCulture),
        _ => Integer.ToString(CultureInfo.InvariantCulture),
    };
}
