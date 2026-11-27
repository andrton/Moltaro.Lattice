using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Child.Loading;

// Keys of effects that capture did not copy. Only an accepted Delete clears
// one. The follower stages them under the writing lock as part of an attempt.
// This separately bounded buffer owns decoded key strings, never frame slices.
internal sealed class MissingUpdateTargets(long memoryLimit)
{
    private Entry[] _entries = [];
    private Entry[]? _grown;

    private Change[] _staged = [];
    private int _stagedCount;
    private int _adds;
    private long _retainedText;
    private long _stagedText;

    public int Count { get; private set; }

    public void Add(EntityTable table, in EntityKey key)
    {
        if (_entries.Length == 0 || _entries[Find(_entries, table, key)].Table is null)
        {
            Stage(new Change(new Entry(table, key), Removes: false), TextBytes(key));
            _adds++;
        }
    }

    public void Delete(EntityTable table, in EntityKey key)
    {
        if (_entries.Length != 0 && _entries[Find(_entries, table, key)] is { Table: not null } entry)
        {
            // Keep the entry we already own, not another spelling decoded from
            // this Delete. NameOnce guarantees no other stage uses this key.
            Stage(new Change(entry, Removes: true), extraText: 0);
        }
    }

    private void Stage(in Change change, long extraText)
    {
        if (_stagedCount == _staged.Length)
        {
            var capacity = NextCapacity(_staged.Length);
            RequireRoom(BytesOf<Change>(capacity) + extraText);
            Array.Resize(ref _staged, capacity);
        }
        else
        {
            RequireRoom(extraText);
        }

        _staged[_stagedCount++] = change;
        _stagedText += extraText;
    }

    // Before publication, reserve room for every addition, including the
    // peak before deleted keys and the preceding array can be let go.
    public void Reserve()
    {
        var count = (long)Count + _adds;
        var capacity = _entries.Length;
        while (count > capacity - (capacity / 4))
        {
            capacity = NextCapacity(capacity);
        }

        if (capacity == _entries.Length)
        {
            return;
        }

        RequireRoom(BytesOf<Entry>(capacity));
        _grown = new Entry[capacity];
        foreach (ref readonly var entry in _entries.AsSpan())
        {
            if (entry.Table is { } table)
            {
                _grown[Find(_grown, table, entry.Key)] = entry;
            }
        }
    }

    public void Commit()
    {
        if (_grown is not null)
        {
            _entries = _grown;
        }

        foreach (ref readonly var change in _staged.AsSpan(0, _stagedCount))
        {
            var entry = change.Entry;
            var place = Find(_entries, entry.Table!, entry.Key);
            if (change.Removes)
            {
                DeleteAt(_entries, place);
                Count--;
                _retainedText -= TextBytes(entry.Key);
            }
            else
            {
                _entries[place] = entry;
                Count++;
            }
        }

        _retainedText += _stagedText;
    }

    public void EndAttempt()
    {
        _grown = null;
        _staged = [];
        _stagedCount = 0;
        _adds = 0;
        _stagedText = 0;
    }

    private void RequireRoom(long extra)
    {
        var held = _retainedText + _stagedText + BytesOf<Entry>(_entries.Length) + BytesOf<Change>(_staged.Length);
        if (extra > memoryLimit - held)
        {
            throw Exhausted();
        }
    }

    private LatticeException Exhausted() => new(LatticeErrorEnum.Unavailable,
        $"The {memoryLimit}-byte load budget of this child cannot hold the missing update targets.");

    private int NextCapacity(int capacity) => capacity switch
    {
        0 => 4,
        <= int.MaxValue / 2 => capacity * 2,
        _ => throw Exhausted(),
    };

    private static long BytesOf<T>(int capacity) => capacity == 0 ? 0 : (3 * IntPtr.Size) + ((long)Unsafe.SizeOf<T>() * capacity);

    private static long TextBytes(in EntityKey key)
    {
        if (key.Text is not { } text)
        {
            return 0;
        }

        // A String holds a length, its UTF-16 code units and the terminator.
        var bytes = (2L * IntPtr.Size) + sizeof(int) + ((text.Length + 1L) * sizeof(char));
        return (bytes + IntPtr.Size - 1) & -IntPtr.Size;
    }

    private static int Home(EntityTable table, in EntityKey key, int mask) =>
        HashCode.Combine(RuntimeHelpers.GetHashCode(table), table.Keys.HashOf(key)) & mask;

    private static int Find(Entry[] entries, EntityTable table, in EntityKey key)
    {
        var mask = entries.Length - 1;
        var place = Home(table, key, mask);
        while (entries[place] is { Table: { } occupied } entry &&
            !(ReferenceEquals(occupied, table) && table.Keys.KeysEqual(entry.Key, key)))
        {
            place = (place + 1) & mask;
        }

        return place;
    }

    // Close the gap in the probe chain without leaving tombstones or taking
    // another array. An entry moves only if its search would meet the gap.
    private static void DeleteAt(Entry[] entries, int gap)
    {
        var mask = entries.Length - 1;
        var place = (gap + 1) & mask;
        while (entries[place] is { Table: { } table } entry)
        {
            var home = Home(table, entry.Key, mask);
            if (((gap - home) & mask) < ((place - home) & mask))
            {
                entries[gap] = entry;
                gap = place;
            }

            place = (place + 1) & mask;
        }

        entries[gap] = default;
    }

    private readonly record struct Entry(EntityTable? Table, EntityKey Key);

    private readonly record struct Change(Entry Entry, bool Removes);
}
