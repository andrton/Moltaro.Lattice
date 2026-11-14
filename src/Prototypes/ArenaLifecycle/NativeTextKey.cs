namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly struct NativeTextKey : IEquatable<NativeTextKey>, IComparable<NativeTextKey>
{
    public NativeTextKey(TextTable owner, TextToken token, string? probe = null)
    {
        Owner = owner;
        Token = token;
        Probe = probe;
    }

    private TextTable Owner { get; }
    public TextToken Token { get; }
    public string? Probe { get; }

    public int CompareTo(NativeTextKey other)
    {
        Check.That(ReferenceEquals(Owner, other.Owner), "Cross-arena index key.");
        return Owner.Compare(Token, Probe, other.Token, other.Probe);
    }

    public bool Equals(NativeTextKey other) => CompareTo(other) == 0;
    public override bool Equals(object? value) => value is NativeTextKey other && Equals(other);
    public override int GetHashCode() => Owner.Hash(Token, Probe);
}
