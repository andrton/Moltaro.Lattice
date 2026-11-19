namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Form 2, bounded: the first eight bytes of the key and the identity, 16 bytes; the root breaks a tie
// of the prefixes. The caveat of form 1 holds for the ties.
internal readonly struct PrefixRootEntry : ITextForm<PrefixRootEntry, PrefixRootOrder>
{
    public PrefixRootEntry(ulong prefix, ulong id)
    {
        Prefix = prefix;
        Id = id;
    }

    public static string Name => "PrefixRoot";

    public static bool IsCandidate => false;

    public static bool SharesText => false;

    public ulong Prefix { get; }

    public ulong Id { get; }

    public static PrefixRootEntry Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress) => new(TextWorld.PrefixOf(text), id);

    public static PrefixRootOrder OrderFor(TextWorld world) => new(world);
}
