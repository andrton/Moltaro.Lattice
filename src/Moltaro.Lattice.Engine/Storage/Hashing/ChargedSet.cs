using System.Numerics;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Hashing;

// The one hash set the engine owns: an open-addressing set on its own array, charged by the bytes
// of that array when it grows (docs/design/storage-and-maintenance.md, the memory budget). The key
// set of a child's publication keys, which it keeps between its transactions, and the index of the
// collection tails by root handle use it. Only the thread that holds the lock of the writes uses
// it; it takes no lock.
//
// The array has a power of two places and is at most half full, so a probe ends at an empty place.
// The probe is linear, and a removal moves the later elements of its chain back, so that no
// tombstone is left and every element stays reachable from its home place. A growth reserves the
// new array while the old one is still charged, and releases the old one once the elements moved.
internal sealed class ChargedSet<T>(IEqualityComparer<T> comparer, MemoryBudget budget)
{
    private Entry[] _entries = [];

    public int Count { get; private set; }

    public bool Contains(T value) => Count != 0 && Find(value, Hash(value), out _);

    // The element equal to the probe, as the set holds it.
    public bool TryGet(T probe, out T value)
    {
        if (Count != 0 && Find(probe, Hash(probe), out var place))
        {
            value = _entries[place].Value;
            return true;
        }

        value = default!;
        return false;
    }

    // False when an equal element is already held.
    public bool Add(T value)
    {
        var hash = Hash(value);
        if (Count != 0 && Find(value, hash, out _))
        {
            return false;
        }

        EnsureCapacity(Count + 1);
        _ = Find(value, hash, out var place);
        _entries[place] = new Entry(true, hash, value);
        Count++;
        return true;
    }

    public bool Remove(T value)
    {
        if (Count == 0 || !Find(value, Hash(value), out var place))
        {
            return false;
        }

        // Moves back each later element of the chain whose home is not between the emptied place and
        // its own, so that a probe from its home still reaches it.
        var mask = _entries.Length - 1;
        var next = place;
        while (true)
        {
            next = (next + 1) & mask;
            ref var entry = ref _entries[next];
            if (!entry.Full)
            {
                break;
            }

            var home = (int)(entry.Hash & (uint)mask);
            var stays = place <= next ? place < home && home <= next : place < home || home <= next;
            if (!stays)
            {
                _entries[place] = entry;
                place = next;
            }
        }

        _entries[place] = default;
        Count--;
        return true;
    }

    // Grows the array, charged before it is allocated, so that count elements fit.
    public void EnsureCapacity(int count)
    {
        if (2L * count <= _entries.Length)
        {
            return;
        }

        var length = (int)Math.Max(Math.Max(4, 2L * _entries.Length), BitOperations.RoundUpToPowerOf2((uint)(2L * count)));
        var previous = _entries;
        var grown = budget.Allocate<Entry>(length);
        foreach (var entry in previous)
        {
            if (entry.Full)
            {
                var place = (int)(entry.Hash & (uint)(length - 1));
                while (grown[place].Full)
                {
                    place = (place + 1) & (length - 1);
                }

                grown[place] = entry;
            }
        }

        _entries = grown;
        budget.Release(previous);
    }

    // Whether an element of this set is in the other, looked up there for each element here. Only reads.
    public bool Overlaps(ChargedSet<T> other)
    {
        foreach (var entry in _entries)
        {
            if (entry.Full && other.Contains(entry.Value))
            {
                return true;
            }
        }

        return false;
    }

    // Removes every element and keeps the array.
    public void Clear()
    {
        Array.Clear(_entries);
        Count = 0;
    }

    // Removes every element and releases the array, for an owner that lets its room go.
    public void Release()
    {
        budget.Release(_entries);
        _entries = [];
        Count = 0;
    }

    public void ReportHeld(List<HeldArray> held)
    {
        if (_entries.Length != 0)
        {
            held.Add(new HeldArray(nameof(ChargedSet<T>), typeof(Entry), _entries.Length));
        }
    }

    // The comparer's hash as it is: a place is taken from its low bits, which the comparers spread
    // (PublicationKeys combines the table and the hash of the key with HashCode.Combine; the slots of
    // the root handles of the collection tails are dense). A multiplication by an odd constant would
    // only permute those low bits, so it would not spread them more.
    private uint Hash(T value) => unchecked((uint)comparer.GetHashCode(value!));

    private bool Find(T value, uint hash, out int place)
    {
        var mask = _entries.Length - 1;
        place = (int)(hash & (uint)mask);
        while (_entries[place] is { Full: true } entry)
        {
            if (entry.Hash == hash && comparer.Equals(entry.Value, value))
            {
                return true;
            }

            place = (place + 1) & mask;
        }

        return false;
    }

    private readonly record struct Entry(bool Full, uint Hash, T Value);
}
