namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Form 1: the entry names the entity, 8 bytes; every comparison reads the key from the root. Correct
// only while the key a root holds is the key the entry was placed by: a replace that changes the key
// publishes a root whose key no longer fits the entry's place, and until the entry moves a reader
// compares against the new key at the old place (see the report of the study).
internal readonly struct RootEntry : ITextForm<RootEntry, RootOrder>
{
    public RootEntry(ulong id) => Id = id;

    public static string Name => "Root";

    public static bool IsCandidate => false;

    public static bool SharesText => false;

    public ulong Id { get; }

    public static RootEntry Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress) => new(id);

    public static RootOrder OrderFor(TextWorld world) => new(world);
}
