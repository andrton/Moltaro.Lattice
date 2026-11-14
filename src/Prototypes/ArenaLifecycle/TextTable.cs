using System.Text;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class TextTable : IDisposable
{
    // Fixture groups stand in for dictionary lookup: the experiment tests text
    // lifetime and shared relocation, not a production string hash table/codec.
    public const int Count = 256;
    private static readonly string[] FixtureText = Enumerable.Range(0, Count)
        .Select(index => $"Group-{index:D3}-λ🚀").ToArray();
    private static readonly byte[][] FixtureBytes = FixtureText.Select(Encoding.UTF8.GetBytes).ToArray();
    private readonly TextEntry[] _entries = new TextEntry[Count];
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly ArenaAllocator _allocator;
    private readonly string[] _values;
    private readonly byte[][] _bytes;

    public TextTable(ArenaAllocator allocator, StringComparison comparison = StringComparison.Ordinal, bool edgeText = false)
    {
        _allocator = allocator;
        Comparison = comparison;
        _values = FixtureText;
        _bytes = FixtureBytes;
        if (edgeText)
        {
            _values = (string[])FixtureText.Clone();
            string[] cases = ["alpha", "ALPHA", "\uE000", "\U00010000", "é", "e\u0301", "Ω", "ω"];
            cases.CopyTo(_values, Count - cases.Length);
            _bytes = _values.Select(Encoding.UTF8.GetBytes).ToArray();
        }
    }

    public StringComparison Comparison { get; }
    public int WaitingWriters => _gate.WaitingWriteCount;
    public static string Expected(int group) => FixtureText[group];
    public string Value(int group) => _values[group];

    public TextToken Prepare(int group)
    {
        GateTrace.EnterWrite(_gate, "text/write");
        try
        {
            ref var entry = ref _entries[group];
            if (!entry.Exists)
            {
                var generation = checked(entry.Generation + 1);
                var bytes = _bytes[group];
                var address = _allocator.Allocate(64, bytes.Length,
                    new BlockOwner(BlockKindEnum.Text, group, generation));
                bytes.CopyTo(_allocator.Span(address, bytes.Length));
                // A text block is independently published to the internal table;
                // prepared references still prevent reclamation before root commit.
                _allocator.Publish(address);
                entry.Generation = generation;
                entry.Address = address;
            }

            entry.Prepared++;
            return new TextToken(group, entry.Generation);
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    public void Publish(TextToken token)
    {
        GateTrace.EnterWrite(_gate, "text/write");
        try
        {
            ref var entry = ref Entry(token);
            Check.That(entry.Prepared > 0, "Missing prepared text claim.");
            entry.Prepared--;
            entry.Stored++;
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    public void Release(TextToken token, bool prepared)
    {
        GateTrace.EnterWrite(_gate, "text/write");
        try
        {
            ref var entry = ref Entry(token);
            if (prepared)
            {
                Check.That(entry.Prepared > 0, "Text prepared count underflow.");
                entry.Prepared--;
            }
            else
            {
                Check.That(entry.Stored > 0, "Text stored count underflow.");
                entry.Stored--;
            }

            if (!entry.Exists)
            {
                _allocator.Free(entry.Address);
            }
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    private ref TextEntry Entry(TextToken token)
    {
        ref var entry = ref _entries[token.Entry];
        Check.That(entry.Exists && entry.Generation == token.Generation, "Stale text lifetime.");
        return ref entry;
    }

    public string? Read(TextToken token, bool copy, Action? heldForTest = null)
    {
        GateTrace.EnterRead(_gate, "text/read");
        try
        {
            ref var entry = ref Entry(token);
            var bytes = _allocator.Span(entry.Address, _bytes[token.Entry].Length);
            Check.That(bytes.SequenceEqual(_bytes[token.Entry]), "Corrupt shared text.");
            heldForTest?.Invoke();
            return copy ? Encoding.UTF8.GetString(bytes) : null;
        }
        finally
        {
            GateTrace.ExitRead(_gate);
        }
    }

    public bool TryMove(AllocationInfo candidate)
    {
        if (!GateTrace.TryWrite(_gate, "text/try-write"))
        {
            return false;
        }

        try
        {
            ref var entry = ref _entries[candidate.Owner.Entry];
            if (!entry.Exists || entry.Generation != candidate.Owner.Generation ||
                entry.Address != candidate.Address ||
                !_allocator.TryReserveMove(candidate, out var destination))
            {
                return false;
            }

            entry.Address = destination;
            _allocator.FinishMove(candidate, destination);
            return true;
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    public void Audit(int[] expected, Dictionary<BlockAddress, BlockOwner> owners, int[]? indexed = null)
    {
        for (var index = 0; index < Count; index++)
        {
            ref var entry = ref _entries[index];
            Check.That(entry.Stored == expected[index] && entry.Prepared == 0 &&
                entry.Indexed == (indexed?[index] ?? 0), "Text reference audit.");
            if (entry.Exists)
            {
                Read(new TextToken(index, entry.Generation), false);
                owners.Add(entry.Address, new BlockOwner(BlockKindEnum.Text, index, entry.Generation));
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    public void RetainIndex(TextToken token)
    {
        GateTrace.EnterWrite(_gate, "text/write");
        try
        {
            Entry(token).Indexed++;
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    public void ReleaseIndex(TextToken token)
    {
        GateTrace.EnterWrite(_gate, "text/write");
        try
        {
            ref var entry = ref Entry(token);
            Check.That(entry.Indexed > 0, "Text index reference underflow.");
            entry.Indexed--;
            if (!entry.Exists)
            {
                _allocator.Free(entry.Address);
            }
        }
        finally
        {
            GateTrace.ExitWrite(_gate);
        }
    }

    // Package callbacks resolve stable descriptors under this gate. No cached
    // address, CLR string per stored key, or ID-based lexical comparison is used.
    private ReadOnlySpan<char> Characters(TextToken token, string? probe, Span<char> scratch)
    {
        if (probe is not null)
        {
            return probe.AsSpan();
        }

        ref var entry = ref Entry(token);
        var count = Encoding.UTF8.GetChars(_allocator.Span(entry.Address, _bytes[token.Entry].Length), scratch);
        return scratch[..count];
    }

    public int Compare(TextToken left, string? leftProbe, TextToken right, string? rightProbe)
    {
        GateTrace.EnterRead(_gate, "text/read");
        try
        {
            Span<char> first = stackalloc char[64];
            Span<char> second = stackalloc char[64];
            return Characters(left, leftProbe, first).CompareTo(Characters(right, rightProbe, second), Comparison);
        }
        finally
        {
            GateTrace.ExitRead(_gate);
        }
    }

    public int Hash(TextToken token, string? probe)
    {
        GateTrace.EnterRead(_gate, "text/read");
        try
        {
            Span<char> scratch = stackalloc char[64];
            return string.GetHashCode(Characters(token, probe, scratch), Comparison);
        }
        finally
        {
            GateTrace.ExitRead(_gate);
        }
    }

    public void HoldComparisonForTest(Action held)
    {
        GateTrace.EnterRead(_gate, "text/read");
        try
        {
            held();
        }
        finally
        {
            GateTrace.ExitRead(_gate);
        }
    }
}
