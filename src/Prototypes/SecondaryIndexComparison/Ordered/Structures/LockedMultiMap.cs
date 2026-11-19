using Arc.Collections;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// The contrast: OrderedMultiMap of Arc.Collections 2.2.0 (MIT), the structure the milestone contract
// named until this study, behind a reader-writer lock, since the package requires external
// synchronization whenever a writer is present. Every read takes the lock shared, every write
// exclusive; a node is a managed object that Add allocates inside the publication.
//
// What it cannot do that the others do: keep a run of equal keys in the order of identities. The map
// keeps the values of one key in the order they were added, and Remove(key, value) scans the run. So a
// read here starts at the first or the last entry of the probe's key and ignores its identity; a seek
// after (key, identity), which S15's walk needs, is not expressible.
internal sealed class LockedMultiMap<TEntry, TKey> : IOrderedIndex<TEntry, TEntry>
    where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
    where TKey : IComparable<TKey>
{
    private readonly OrderedMultiMap<TKey, ulong> _map = new();

    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    public string Name => "ArcOrderedMultiMap+ReaderWriterLockSlim";

    public long Count => _map.Count;

    // The managed bytes of the nodes are measured by the scenario on the managed heap.
    public StructureBytes Bytes => new(0, 0, _map.Count, 0);

    public void Prepare(int writes)
    {
        // Nothing to take: Add allocates its node inside the publication.
    }

    public void Withdraw(int writes)
    {
    }

    public void AfterPublication()
    {
    }

    public void DrainAll()
    {
    }

    public void Insert(in TEntry entry)
    {
        _gate.EnterWriteLock();

        try
        {
            _map.Add(entry.Key, entry.Id);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public bool Remove(in TEntry entry)
    {
        _gate.EnterWriteLock();

        try
        {
            return _map.Remove(entry.Key, entry.Id);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public int Read(in TEntry from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into)
    {
        _gate.EnterReadLock();

        try
        {
            var copied = 0;
            var node = forward ? _map.GetLowerBound(from.Key) : _map.GetUpperBound(from.Key);

            while (node is not null && copied < into.Length)
            {
                if (keyOnly && node.Key.CompareTo(from.Key) != 0)
                {
                    break;
                }

                into[copied++] = TEntry.With(node.Key, node.Value);
                node = forward ? node.Next : node.Previous;
            }

            return copied;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public List<TEntry> Validate()
    {
        var entries = new List<TEntry>(_map.Count);

        for (var node = _map.First; node is not null; node = node.Next)
        {
            if (entries.Count > 0 && entries[^1].Key.CompareTo(node.Key) > 0)
            {
                throw new InvalidOperationException("The map is out of key order.");
            }

            entries.Add(TEntry.With(node.Key, node.Value));
        }

        return entries;
    }

    public void Dispose() => _gate.Dispose();
}
