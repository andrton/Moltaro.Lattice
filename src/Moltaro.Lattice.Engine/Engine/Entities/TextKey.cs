using System.Diagnostics;

namespace Moltaro.Lattice.Engine.Entities;

// A key of the primary index of an entity type whose key is a String: in the index, the slot of the
// root entry it belongs to; for a lookup, the text looked for. A key in the index is compared with the
// text of the key stored in its root, through the root block of the entry, so the index holds no copy
// of the text (decision on index structures: a String key needs neither a managed string per lookup
// nor a permanent duplicate of all key text). 16 bytes.
//
// Equality and hash are those of the comparison the key field declares, Ordinal or OrdinalIgnoreCase,
// and come from the comparison of the schema (Comparison.ValueComparer): the hash is computed from
// the managed string, when the key is made for an insert and for a lookup (EntityKey.TextHash), and
// carried by every key, so the text in the arena is never hashed; the comparison of a lookup key with
// a key in the index is the table's, EntityTable.KeyEquals.
//
// The index holds every key once under its comparison, because a write adds a key only after it found
// it absent under that comparison. So two keys in the index are equal exactly when they name the same
// slot, and only a lookup key is ever compared with stored text; the index never compares two lookup
// keys. That comparison reads the root block of a live entry while the catalog gate is held, and a
// write changes the root block of an indexed entry only while it holds the catalog gate exclusively
// (Protection.RootGates).
internal readonly struct TextKey : IEquatable<TextKey>
{
    // The slot of a lookup key, which no entry has: the table of root entries ends below 2^31.
    private const uint LookupSlot = uint.MaxValue;

    // The table of the entity type for a key in the index, the text for a lookup key.
    private readonly object _reference;

    private readonly uint _slot;

    private readonly int _hash;

    private TextKey(object reference, uint slot, int hash)
    {
        _reference = reference;
        _slot = slot;
        _hash = hash;
    }

    // The key of the root in the slot, whose key has the hash.
    public static TextKey Stored(EntityTable table, uint slot, int hash) => new(table, slot, hash);

    // The key to look the text up with, whose hash is given.
    public static TextKey Lookup(string text, int hash) => new(text, LookupSlot, hash);

    public bool Equals(TextKey other)
    {
        if (_hash != other._hash)
        {
            return false;
        }

        if (_slot == LookupSlot)
        {
            return other._slot == LookupSlot
                ? throw new UnreachableException("The index compares a lookup key only with the keys it holds.")
                : ((EntityTable)other._reference).KeyEquals(other._slot, (string)_reference);
        }

        return other._slot == LookupSlot
            ? ((EntityTable)_reference).KeyEquals(_slot, (string)other._reference)
            : _slot == other._slot;
    }

    public override bool Equals(object? obj) => obj is TextKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
