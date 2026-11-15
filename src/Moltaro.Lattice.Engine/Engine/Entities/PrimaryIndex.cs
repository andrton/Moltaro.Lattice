using System.Diagnostics;
using System.Runtime.CompilerServices;
using Arc.Collections;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// The primary index of one entity type: its keys, each mapped to the handle of the root entry of
// its live root (decision on index structures, "Selected implementation: Arc.Collections
// UnorderedMapSlim"). It holds no copy of an object, only the key in the form TKey gives and the
// 8 bytes of the handle: an int or a long for an integer key, a Guid, or for a String key a TextKey
// of 16 bytes (KeyKindEnum). With the hash and the link the package keeps beside them, and its bucket,
// a slot takes 24 bytes for an int, 28 for a long and 36 for a Guid or a TextKey. The equality and the
// hash of a key are those of TKey, which for a TextKey come from the comparison of the schema.
//
// The index takes no lock. Lookups read it under the catalog gate held shared; the thread that
// executes a write is the only one that changes it, under the catalog gate held exclusively, and
// reads it without the gate (Protection.RootGates).
//
// Room before publication. The package grows its arrays when it adds a key to a full map, and
// publication must not allocate (milestone contract, "Index preparation and traversal"). So a write
// calls Reserve first, and Add then never grows the map. Until S08 the index grows under exclusive
// protection: Reserve builds the larger map while the caller holds the catalog gate exclusively
// (brief of S06, Acceptance); S08 builds it while lookups use the old one.
//
// Version changes with every change of the map, an added or a removed key and a growth, so that an
// enumeration sees that the index changed under it (brief of S06, "Enumeration until S08").
internal sealed class PrimaryIndex<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private UnorderedMapSlim<TKey, RootHandle> _map = new();

    public int Version { get; private set; }

    // The bytes the index holds: for every slot of the map, its node and its bucket, the elements of
    // the two arrays of the package. The objects around them, a few dozen bytes for each entity
    // type, are not counted.
    public long Bytes => (long)_map.Capacity * (Unsafe.SizeOf<UnorderedMapSlim<TKey, RootHandle>.Node>() + sizeof(int));

    // Whether Add can add a key without growing the map.
    public bool HasRoom => _map.Count < _map.Capacity;

    public bool TryFind(TKey key, out RootHandle handle) => _map.TryGetValue(key, out handle);

    // Grows the map to twice its capacity when it has no room for one more key. The caller holds
    // the catalog gate exclusively. An OutOfMemoryException leaves the old map in place.
    public void Reserve()
    {
        if (HasRoom)
        {
            return;
        }

        var grown = new UnorderedMapSlim<TKey, RootHandle>((uint)_map.Capacity * 2);

        foreach (var entry in _map)
        {
            grown.TryAdd(entry.Key, entry.Value);
        }

        _map = grown;
        Version++;
    }

    // Adds a key that is absent, after Reserve. The caller holds the catalog gate exclusively.
    public void Add(TKey key, RootHandle handle)
    {
        Debug.Assert(HasRoom, "Reserve made room before the publication.");

        var added = _map.TryAdd(key, handle);

        Debug.Assert(added, "A write adds a key only after it found it absent.");
        Version++;
    }

    // Removes a key that is there. The caller holds the catalog gate exclusively.
    public void Remove(TKey key)
    {
        var removed = _map.Remove(key);

        Debug.Assert(removed, "A write removes a key only after it found it.");
        Version++;
    }

    // The handle of the next key from position on, in the order of the nodes of the map, and the
    // position after it; false when there is none. The caller holds the catalog gate; between two
    // calls the map may change, which Version tells.
    public bool TryFindNext(ref int position, out RootHandle handle)
    {
        var (nodes, used) = _map.UnsafeGetNodes();

        while (position < used)
        {
            ref var node = ref nodes[position++];

            if (node.IsInUse())
            {
                handle = node.Value;

                return true;
            }
        }

        handle = default;

        return false;
    }
}
