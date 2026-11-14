using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

// The arena catalog gate owns every package access. Package nodes never escape
// to a public reader; callers pin root lifetimes before releasing that gate.
internal sealed class ArenaIndexes
{
    private readonly UnorderedMapSlim<int, RootClaim> _primary = new(ArenaModel.KeyLimit);
    private readonly OrderedMultiMap<CompositeIndexKey, RootClaim> _secondary = new();
    private readonly OrderedMultiMap<CompositeIndexKey, RootClaim> _staging = new();
    private readonly TextTable _texts;

    public ArenaIndexes(TextTable texts) => _texts = texts;
    public int Count => _primary.Count;
    public int PrimaryCapacity => _primary.Capacity;
    public int SecondaryCount => _secondary.Count;
    public int Lookup(int key) => _primary.TryGetValue(key, out var claim) ? claim.Entry : -1;
    public CompositeIndexKey Stored(TextToken text, long number) => new(new NativeTextKey(_texts, text), number);
    public CompositeIndexKey Probe(string text, long number) => new(new NativeTextKey(_texts, default, text), number);

    public PreparedIndexEntry Prepare(RootClaim claim, CompositeIndexKey next, CompositeIndexKey? previous)
    {
        OrderedMultiMap<CompositeIndexKey, RootClaim>.Node node;
        if (previous is { } old)
        {
            node = _secondary.FindNode(old, claim) ?? throw new InvalidOperationException("Missing secondary source.");
        }
        else
        {
            Check.That(_primary.Count < _primary.Capacity, "Primary capacity must exist before publication.");
            node = _staging.Add(next, claim).Node;
            _staging.RemoveNode(node);
        }

        _texts.RetainIndex(next.Text.Token);
        return new PreparedIndexEntry(node, next, claim, previous is null);
    }

    public void Abort(PreparedIndexEntry prepared) => _texts.ReleaseIndex(prepared.Key.Text.Token);

    public void Publish(int key, PreparedIndexEntry prepared)
    {
        if (!prepared.Insertion)
        {
            var oldText = prepared.Node.Key.Text.Token;
            Check.That(prepared.Node.Value == prepared.Claim, "Prepared node lifetime changed.");
            _secondary.RemoveNode(prepared.Node);
            // SetNodeKey may retain the old descriptor when values compare equal.
            // Always reinstall the prepared descriptor, even for an equal key.
            _secondary.Add(prepared.Key, prepared.Claim, prepared.Node);
            _texts.ReleaseIndex(oldText);
        }
        else
        {
            Check.That(_primary.TryAdd(key, prepared.Claim), "Duplicate primary publication.");
            _secondary.Add(prepared.Key, prepared.Claim, prepared.Node);
        }
    }

    public void Remove(int key, RootClaim claim, CompositeIndexKey previous)
    {
        var node = _secondary.FindNode(previous, claim) ?? throw new InvalidOperationException("Missing delete index entry.");
        var text = node.Key.Text.Token;
        _secondary.RemoveNode(node);
        Check.That(_primary.Remove(key), "Missing primary removal.");
        _texts.ReleaseIndex(text);
    }

    public void ChangeNumber(RootClaim claim, CompositeIndexKey previous, long number)
    {
        var node = _secondary.FindNode(previous, claim) ?? throw new InvalidOperationException("Missing numeric source.");
        _secondary.RemoveNode(node);
        _secondary.Add(previous with { Number = number }, claim, node);
    }

    public int Collect(CompositeIndexKey lower, CompositeIndexKey upper, Span<RootClaim> destination)
    {
        var count = 0;
        for (var node = _secondary.GetLowerBound(lower); node is not null && node.Key.CompareTo(upper) <= 0; node = node.Next)
        {
            Check.That(count < destination.Length, "Bounded candidate buffer exhausted.");
            destination[count++] = node.Value;
        }

        return count;
    }

    public void Audit(ValueSpec?[] expected, RootEntry[] entries, int[] references)
    {
        var count = expected.Count(value => value is not null);
        Check.That(Count == count && SecondaryCount == count, "Index cardinality mismatch.");
        var seen = new HashSet<RootClaim>();
        CompositeIndexKey? previous = null;
        foreach (var pair in _secondary)
        {
            Check.That(seen.Add(pair.Value), "Duplicate secondary root.");
            var entry = entries[pair.Value.Entry];
            Check.That(entry.Live && entry.Generation == pair.Value.Generation, "Stale secondary root.");
            var value = expected[entry.Key] ?? throw new InvalidOperationException("Unexpected indexed key.");
            Check.That(_primary.TryGetValue(entry.Key, out var primary) && primary == pair.Value,
                "Primary locator mismatch.");
            Check.That(pair.Key.Text.Probe is null && pair.Key.Text.Token.Entry == value.TextGroup &&
                pair.Key.Number == value.Number, "Secondary payload mismatch.");
            if (previous is { } prior)
            {
                Check.That(prior.CompareTo(pair.Key) <= 0, "Secondary order mismatch.");
            }

            references[pair.Key.Text.Token.Entry]++;
            previous = pair.Key;
        }
    }
}
