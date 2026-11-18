namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Form 3: the relative address of the shared text entry and the identity, 16 bytes; every comparison
// reads the text where the entry lies. The root names the same entry, so its text is out of line
// whatever its length.
internal readonly struct SharedEntry : ITextForm<SharedEntry, SharedOrder>
{
    public SharedEntry(ulong text, ulong id)
    {
        Text = text;
        Id = id;
    }

    public static string Name => "Shared";

    public static bool SharesText => true;

    public ulong Text { get; }

    public ulong Id { get; }

    public static SharedEntry Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress) => new(textAddress, id);

    public static SharedOrder OrderFor(TextWorld world) => new(world);
}
